// Offline checks of runCode's compiler (Civil3D-MCP-Plugin/ScriptCompiler.cs).
// Build the plugin first, then run on the framework of that Civil 3D release:
//   2026 (net8):  dotnet run --project tests/RunCodeHarness -f net8.0 -- [<refs>] [<Civil3DMcpPlugin.dll>]
//   2027 (net10): dotnet run --project tests/RunCodeHarness -f net10.0 -- <refs> <net10 Civil3DMcpPlugin.dll>
// Defaults: %LOCALAPPDATA%\Civil3D-mcp\refs-2026 and the net8.0-windows Release
// build. Prints SKIPPED (exit 0) when either is missing; exit 1 on a failure.
using Civil3DMcpPlugin;

var repoRoot = FindRepoRoot();
var refsFolder = args.Length > 0
  ? args[0]
  : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3D-mcp", "refs-2026");
var pluginDll = args.Length > 1
  ? args[1]
  : Path.Combine(repoRoot, "Civil3D-MCP-Plugin", "bin", "Release", "net8.0-windows", "Civil3DMcpPlugin.dll");

if (!File.Exists(Path.Combine(refsFolder, "AcDbMgd.dll")) || !File.Exists(pluginDll))
{
  Console.WriteLine($"SKIPPED: needs the Civil 3D references ({refsFolder}) and the built plugin ({pluginDll}).");
  return 0;
}

// The framework this harness runs on (like Civil 3D's), WPF/WinForms for
// acmgd's signatures, the Civil 3D references and the plugin (RunCodeScript).
var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var desktopDirectory = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(frameworkDirectory)!)!, "Microsoft.WindowsDesktop.App", Path.GetFileName(frameworkDirectory));
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
  .Where(path => path.StartsWith(frameworkDirectory, StringComparison.OrdinalIgnoreCase))
  .Concat(Directory.Exists(desktopDirectory) ? Directory.GetFiles(desktopDirectory, "*.dll") : Array.Empty<string>())
  .Concat(Directory.GetFiles(refsFolder, "*.dll"))
  .Append(pluginDll)
  .Where(IsManagedAssembly)
  .ToList();

var failures = 0;
var run = 0;

ScriptCompileResult Compile(string code, bool write = false) =>
  ScriptCompiler.Compile(code, $"HarnessRun_{++run}", references, write);

void Expect(bool condition, string name, ScriptCompileResult? result = null)
{
  if (condition)
  {
    Console.WriteLine($"ok   {name}");
    return;
  }

  failures++;
  Console.WriteLine($"FAIL {name}");
  foreach (var error in result?.Errors ?? new List<ScriptDiagnostic>())
  {
    Console.WriteLine($"       line {error.Line}:{error.Column} {error.Code} {error.Message}");
  }
}

bool HasError(ScriptCompileResult result, string code, int line) =>
  result.Errors.Any(error => error.Code == code && error.Line == line);

// The obstacle-sampling test of 2026-10-11 (Sample.cs) as a runCode script.
var sample = """
  var dwg = (string)Args["dwg"];
  double r = 40;
  var cells = new HashSet<(int, int)>();
  bool Hit(double x, double y) => cells.Contains(((int)(x / r), (int)(y / r)));
  var side = OpenSide(dwg);
  long n = 0, ents = 0;
  var sb = new StringBuilder();
  foreach (var e in ModelSpace<Entity>(side))
  {
    ents++;
    if (e is Hatch || e is Solid || e is RasterImage || e is Wipeout) continue;
    Extents3d ext; try { ext = e.GeometricExtents; } catch (Exception) { continue; }
    if (e is Curve cv)
    {
      double len; try { len = cv.GetDistanceAtParameter(cv.EndParam); } catch (AcException) { continue; }
      for (double s = 0; s <= len; s += 0.8) { var p = cv.GetPointAtDist(s); if (Hit(p.X, p.Y)) { sb.Append(p.X.ToString("F2")).Append(' ').Append(p.Y.ToString("F2")).Append('\n'); n++; } }
    }
  }
  Log($"{ents} objects, {n} points");
  return new { objects = ents, points = n, file = WriteFile("points.txt", sb.ToString()) };
  """;
var sampleResult = Compile(sample);
Expect(sampleResult.Succeeded, "the obstacle-sampling script compiles in read mode", sampleResult);

var civil = Compile("""
  var names = new List<string>();
  foreach (ObjectId id in Civil.GetAlignmentIds()) names.Add(Open<Alignment>(id).Name);
  Surface surface = null;
  CivilEntity civilEntity = null;
  return names;
  """);
Expect(civil.Succeeded, "Civil 3D namespaces and the Entity/Surface aliases resolve", civil);

var syntax = Compile("var a = 1;\nvar b = 2;\nvar c = a + ;\nreturn c;");
Expect(!syntax.Succeeded && syntax.Errors.All(error => error.Line == 3), "a syntax error is reported on its snippet line (3)", syntax);

var semantic = Compile("// comment\nvar x = 1;\n\nx.NoSuchMember();");
Expect(HasError(semantic, "CS1061", 4), "a semantic error is reported on its snippet line (4)", semantic);

var hoisted = Compile("using System.Numerics;\nusing Sb = System.Text.StringBuilder;\nvar v = new Vector2(1, 2);\nreturn new Sb().Append(v.X).ToString();");
Expect(hoisted.Succeeded, "leading using directives are moved out of the method", hoisted);

var commentedUsing = Compile("using System.Numerics; // vectors\nreturn new Vector2(1, 2).X;");
Expect(commentedUsing.Succeeded, "a using line with a trailing comment is moved out too", commentedUsing);

var badUsing = Compile("using System.Text;\nusing Nope.Nowhere;\nreturn 1;");
Expect(HasError(badUsing, "CS0246", 2), "an unknown namespace in a using is reported on its line (2)", badUsing);

var usingStatement = Compile("using var stream = new MemoryStream();\nusing (var other = new MemoryStream()) { }\nreturn stream.Length;");
Expect(usingStatement.Succeeded, "using statements stay in the method", usingStatement);

const string forWrite = "foreach (var e in ModelSpace<Entity>()) { var w = Tr.GetObject(e.ObjectId, OpenMode.ForWrite); }\nreturn null;";
var readWrite = Compile(forWrite);
Expect(HasError(readWrite, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses OpenMode.ForWrite", readWrite);
Expect(Compile(forWrite, write: true).Succeeded, "write mode allows OpenMode.ForWrite");

var upgrade = Compile("var e = Open<Entity>(ObjectId.Null);\ne.UpgradeOpen();\ne.Erase();");
Expect(HasError(upgrade, ScriptCompiler.GuardDiagnosticId, 2), "read mode refuses UpgradeOpen/Erase", upgrade);

var transaction = Compile("using var t = Db.TransactionManager.StartTransaction();\nreturn 1;");
Expect(HasError(transaction, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses a regular transaction", transaction);
Expect(Compile("using var t = Db.TransactionManager.StartOpenCloseTransaction();\nreturn 1;").Succeeded, "read mode allows an open-close transaction");

var databaseSetter = Compile("Db.Ltscale = 2;");
Expect(HasError(databaseSetter, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses setting a Database property", databaseSetter);
Expect(Compile("Db.Ltscale = 2;", write: true).Succeeded, "write mode allows setting a Database property");

var memoryEntity = Compile("var pl = new Polyline();\npl.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);\npl.Closed = true;\nreturn pl.Area;");
Expect(memoryEntity.Succeeded, "read mode allows building an entity in memory", memoryEntity);

var civilCreate = Compile("Civil.CogoPoints.Add(new Point3d(0, 0, 0), true);");
Expect(HasError(civilCreate, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses a Civil 3D Add", civilCreate);

var append = Compile("var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db));\nms.AppendEntity(new Line());");
Expect(HasError(append, ScriptCompiler.GuardDiagnosticId, 2), "read mode refuses AppendEntity", append);

var prompt = Compile("var p = Ed.GetPoint(\"Pick\");\nreturn p.Value;", write: true);
Expect(HasError(prompt, ScriptCompiler.GuardDiagnosticId, 1), "both modes refuse Editor prompts", prompt);

var dialog = Compile("Application.ShowAlertDialog(\"hi\");", write: true);
Expect(HasError(dialog, ScriptCompiler.GuardDiagnosticId, 1), "both modes refuse modal dialogs", dialog);

var subscribe = Compile("Db.ObjectModified += (s, e) => { };", write: true);
Expect(HasError(subscribe, ScriptCompiler.GuardDiagnosticId, 1), "both modes refuse event subscriptions", subscribe);

var ownCommit = Compile("var x = 1;\nTr.Commit();", write: true);
Expect(HasError(ownCommit, ScriptCompiler.GuardDiagnosticId, 2), "both modes refuse committing the run's Tr", ownCommit);

var sideAbort = Compile("TransactionFor(Db).Abort();");
Expect(HasError(sideAbort, ScriptCompiler.GuardDiagnosticId, 1), "both modes refuse aborting a run transaction", sideAbort);

Expect(Compile("using var t = Db.TransactionManager.StartTransaction();\nt.Commit();", write: true).Succeeded, "write mode allows committing a transaction the snippet started");

var writeOpenClose = Compile("using var t = Db.TransactionManager.StartOpenCloseTransaction();\nreturn 1;", write: true);
Expect(HasError(writeOpenClose, ScriptCompiler.GuardDiagnosticId, 1), "write mode refuses open-close transactions (they commit outside Tr)", writeOpenClose);

var writeOpen = Compile("var o = Db.BlockTableId.Open(OpenMode.ForRead);", write: true);
Expect(HasError(writeOpen, ScriptCompiler.GuardDiagnosticId, 1), "write mode refuses ObjectId.Open", writeOpen);

var extract = Compile("var s = Open<TinSurface>(ObjectId.Null);\nreturn s.ExtractContours(1.0).Count;");
Expect(HasError(extract, ScriptCompiler.GuardDiagnosticId, 2), "read mode refuses Civil 3D Extract* (adds entities)", extract);

var explode = Compile("Open<BlockReference>(ObjectId.Null).ExplodeToOwnerSpace();");
Expect(HasError(explode, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses ExplodeToOwnerSpace", explode);

var layout = Compile("LayoutManager.Current.CreateLayout(\"X\");");
Expect(HasError(layout, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses LayoutManager changes", layout);

var addObject = Compile("Db.AddDBObject(new Xrecord());");
Expect(HasError(addObject, ScriptCompiler.GuardDiagnosticId, 1), "read mode refuses Database.AddDBObject", addObject);

var returns = Compile("if (Args.Count > 0) return 1;\nreturn 2;");
Expect(returns.Succeeded && returns.Warnings.All(warning => warning.Code != "CS0162"), "a snippet that returns compiles without unreachable-code warnings", returns);

Expect(ScriptCompiler.FindScriptLine("   at Script_X.Run() in script:line 17\n   at Other() in other.cs:line 3") == 17, "stack traces map back to the snippet line");

var unique = Compile("return 1;");
var again = Compile("return 1;");
Expect(unique.Succeeded && again.Succeeded && unique.TypeName != again.TypeName, "each run gets its own class and assembly name");

Console.WriteLine(failures == 0 ? $"PASS ({references.Count} references)" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static bool IsManagedAssembly(string path)
{
  try
  {
    System.Reflection.AssemblyName.GetAssemblyName(path);
    return true;
  }
  catch (Exception)
  {
    return false;
  }
}

static string FindRepoRoot()
{
  for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
  {
    if (Directory.Exists(Path.Combine(directory.FullName, "Civil3D-MCP-Plugin")))
    {
      return directory.FullName;
    }
  }

  return Directory.GetCurrentDirectory();
}
