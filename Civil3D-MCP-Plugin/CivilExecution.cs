using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  // Where an operation runs. CommandContext is the normal route
  // (DocumentManager.ExecuteInCommandContextAsync). Once that route is wedged
  // (see RunOnHostAsync), reads run from an Application.Idle tick and
  // everything that changes the drawing or drives commands runs inside the
  // plugin's own C3DMCPHOP command.
  private enum HostRoute
  {
    CommandContext,
    Idle,
    PluginCommand,
  }

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      // With zero documents open there is no command context, so the
      // ExecuteInCommandContextAsync callback below never runs and the
      // NO_DRAWING check inside it is unreachable. Hanging here would hold
      // HostExecutionGate forever and wedge every later host operation, so
      // fail fast before the hop. The inner check stays because the document
      // can still close while this request waits in the queue.
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      T? result = default;
      Exception? capturedException = null;

      // Read on the request's thread: the expected identity is an AsyncLocal,
      // and the Idle and plugin-command routes run on the UI thread's
      // execution context, where it would read empty and skip the check.
      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();

      var hostTask = RunOnHostAsync(route =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = RequireExpectedDocument(expectedDrawingIdentity, "No drawing changes were made.");
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          // Outside a command (the Idle route), a document lock plus a regular
          // Transaction wipes the drawing's undo history even when it is only
          // read and aborted; an OpenCloseTransaction does not. Only reads take
          // the Idle route: writes run in a command (PluginCommand) so they
          // are undoable.
          using var transaction = route == HostRoute.Idle
            ? database.TransactionManager.StartOpenCloseTransaction()
            : database.TransactionManager.StartTransaction();

          result = action(doc, civilDoc, database, transaction);

          if (write)
          {
            transaction.Commit();
          }
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }

        return Task.CompletedTask;
      }, needsCommand: write, cancellationToken);

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  // Same host gate, command context, drawing-identity check, and document
  // lock as ExecuteAsync, but WITHOUT an enclosing transaction. Some database
  // operations (xref reload/unload/bind/detach) manage their own internal
  // transactions and must not run nested inside an open top transaction; the
  // callback opens short transactions of its own where it needs to read.
  public static async Task<T> ExecuteLockedWithoutTransactionAsync<T>(Func<Document, CivilDocument, Database, T> action)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      T? result = default;
      Exception? capturedException = null;
      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();

      var hostTask = RunOnHostAsync(_ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = RequireExpectedDocument(expectedDrawingIdentity, "No drawing changes were made.");
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");

          using var documentLock = doc.LockDocument();
          result = action(doc, civilDoc, doc.Database);
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }

        return Task.CompletedTask;
      }, needsCommand: true, cancellationToken);

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      T? result = default;
      Exception? capturedException = null;

      Func<object, Task> callback = async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          result = await action();
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      };

      // Always run application-context work from the Idle hop, never from a
      // command context. The only caller is newDrawing, whose DocumentManager
      // .Add is an application-context API that needs no command context, and
      // choosing the hop by MdiActiveDocument was racy: if the last document
      // closed between the check and the hop, the command-context callback
      // would never run and the request would hang holding the gate.
      var hostTask = ExecuteInApplicationContextAsync(callback, cancellationToken);

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  // Runs an async action that drives AutoCAD commands (Editor.CommandAsync)
  // for the active document, serialized behind the same host gate as every
  // other drawing operation. Unlike ExecuteAsync it deliberately opens no
  // transaction and takes no explicit document lock around the action: a
  // command such as -PLOT or -PUBLISH must own the document itself, and
  // holding an open transaction across it is what makes the .NET-only paths
  // unsafe (see PlotCommands.cs). Callers open short, disposed read
  // transactions of their own before issuing commands.
  public static async Task<T> ExecuteCommandSequenceAsync<T>(Func<Document, CancellationToken, Task<T>> action)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      // Same zero-document fast fail as ExecuteAsync: the command-context hop
      // below never fires without a document and would wedge the gate.
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      T? result = default;
      Exception? capturedException = null;
      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();

      var hostTask = RunOnHostAsync(async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = RequireExpectedDocument(expectedDrawingIdentity, "No commands were run.");
          result = await action(doc, cancellationToken);
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      }, needsCommand: true, cancellationToken);

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  // True once the command-context route stopped running callbacks; reported
  // by getCivil3DHealth.
  public static bool CommandContextWedged => _commandContextWedged;

  private static Document RequireExpectedDocument(string? expectedDrawingIdentity, string noChanges)
  {
    var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
    var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
    if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
        !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. {noChanges}");
    }

    return doc;
  }

  // How long a command-context hop may take to start while Civil 3D is idle
  // before it is treated as wedged, and how long a running command or LISP
  // prompt may hold the host before the request fails with HOST_BUSY.
  private static readonly TimeSpan CommandContextStartGrace = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan HostBusyTimeout = TimeSpan.FromSeconds(15);

  // Set once a command-context hop failed to start although no command was
  // active. AutoCAD does not recover from that state until it restarts:
  //  - 2026-09-25 (jorgegarcias60/Civil3D-mcp#1): a LISP expression sent over
  //    COM waited for input, a request queued behind it, and after the prompt
  //    was cancelled every later ExecuteInCommandContextAsync stayed queued;
  //  - 2026-10-10: a single COM SendCommand("(princ) ") from another process,
  //    with no prompt left open, is enough. Afterwards Application.Idle keeps
  //    firing (about 120 ticks a second), but no command-context callback
  //    runs, not even one queued from the main thread, and WM_NULL,
  //    WM_MOUSEMOVE, key messages, another COM command or switching the
  //    active document do not bring the route back.
  private static volatile bool _commandContextWedged;

  // Runs the operation on the host's main thread exactly once. The normal
  // route is the command-context hop; an Application.Idle watcher runs next to
  // it and
  //  - fails fast with CIVIL3D.HOST_BUSY while a command or prompt stays active
  //    (CMDACTIVE != 0), or while no Idle tick arrives at all (a modal dialog),
  //    instead of letting the request time out after 120 s;
  //  - takes over when the command-context hop has not started within the
  //    grace period although Civil 3D is idle: from then on reads run from the
  //    Idle tick, and operations that write or run commands (needsCommand)
  //    run inside the plugin's C3DMCPHOP command, started through COM.
  // A claim flag makes sure whichever hop comes second does nothing, including
  // a command-context callback that AutoCAD runs long after the request ended.
  //
  // The watcher, the HOST_BUSY checks and the claim flag are a port of
  // jorgegarcias60/Civil3D-mcp#1 and #2; the plugin-command route and the
  // OpenCloseTransaction for Idle reads are this fork's.
  private static async Task RunOnHostAsync(Func<HostRoute, Task> operation, bool needsCommand, CancellationToken cancellationToken)
  {
    var claimed = 0;
    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    async Task RunOnce(HostRoute route)
    {
      if (Interlocked.Exchange(ref claimed, 1) != 0)
      {
        return;
      }

      try
      {
        await operation(route);
        done.TrySetResult(true);
      }
      catch (Exception ex)
      {
        done.TrySetException(ex);
      }
    }

    var useCommandContext = !_commandContextWedged;
    if (useCommandContext)
    {
      try
      {
        var commandTask = RunInCommandContextAsync(_ => RunOnce(HostRoute.CommandContext));
        _ = commandTask.ContinueWith(
          t => PluginLog.Warn("Host", "Command-context hop failed; the Idle watcher takes over.", t.Exception),
          CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted,
          TaskScheduler.Default);
      }
      catch (Exception ex)
      {
        PluginLog.Warn("Host", "Command-context hop could not be queued; the Idle watcher takes over.", ex);
      }
    }

    // Busy and idle time are measured separately: only continuous idle time
    // counts toward the grace period, so a command that runs for a few seconds
    // does not make a healthy command-context hop look wedged.
    DateTime? busySince = null;
    DateTime? idleSince = null;
    // Last Idle tick (UTC ticks), read by the timer thread: no tick at all for
    // HostBusyTimeout means the host is inside a modal dialog or a long
    // operation, where neither hop can run.
    long lastIdleTicks = DateTime.UtcNow.Ticks;
    var fallbackStarted = 0;
    EventHandler? idle = null;
    idle = async (_, _) =>
    {
      // async void: an exception escaping this handler would take down the
      // host, so every failure ends the request instead.
      try
      {
        Interlocked.Exchange(ref lastIdleTicks, DateTime.UtcNow.Ticks);
        if (Volatile.Read(ref claimed) != 0 || Volatile.Read(ref fallbackStarted) != 0)
        {
          CoreApp.Idle -= idle;
          return;
        }

        var now = DateTime.UtcNow;
        // A LISP prompt can wait with CMDACTIVE 0; the plugin-command route
        // would type its command into that prompt, so it also needs a
        // quiescent editor.
        var commandActive = Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE")) != 0
          || (needsCommand && App.DocumentManager.MdiActiveDocument?.Editor.IsQuiescent == false);
        if (commandActive)
        {
          idleSince = null;
          busySince ??= now;
          if (now - busySince.Value >= HostBusyTimeout && Interlocked.Exchange(ref claimed, 1) == 0)
          {
            CoreApp.Idle -= idle;
            done.TrySetException(new JsonRpcDispatchException(
              "CIVIL3D.HOST_BUSY",
              "Civil 3D has a command or prompt in progress (CMDACTIVE is not 0). Finish it or press Esc in Civil 3D, then retry. No drawing changes were made."));
          }

          return;
        }

        busySince = null;
        idleSince ??= now;
        if (useCommandContext && now - idleSince.Value < CommandContextStartGrace)
        {
          return;
        }

        CoreApp.Idle -= idle;
        Interlocked.Exchange(ref fallbackStarted, 1);
        if (useCommandContext && !_commandContextWedged)
        {
          _commandContextWedged = true;
          // Also seen with a plain LISP prompt at the Command line (getstring
          // reports CMDACTIVE 0), so this is not always a broken host.
          PluginLog.Warn("Host", $"Command-context hop did not start within {CommandContextStartGrace.TotalSeconds:0} s with no command active (after a COM client ran a command, a LISP prompt at the Command line, or a wedged host); reads now run from Application.Idle and writes and commands from the C3DMCPHOP command until Civil 3D restarts.");
        }

        if (needsCommand)
        {
          await StartPluginCommandAsync(() => RunOnce(HostRoute.PluginCommand), () => Volatile.Read(ref claimed) != 0);
        }
        else
        {
          await RunOnce(HostRoute.Idle);
        }
      }
      catch (Exception ex)
      {
        CoreApp.Idle -= idle;
        if (Interlocked.Exchange(ref claimed, 1) == 0)
        {
          done.TrySetException(ex);
        }
      }
    };
    CoreApp.Idle += idle;

    // Idle is raised when the host's message queue empties; a background
    // Civil 3D with nothing to process may not raise it again. Post a no-op
    // message to the main window while the request waits so the watcher keeps
    // getting ticks. PostMessage is safe from any thread.
    IntPtr mainWindow;
    using (var host = System.Diagnostics.Process.GetCurrentProcess())
    {
      mainWindow = host.MainWindowHandle;
    }
    using var nudge = new Timer(_ =>
    {
      if (Volatile.Read(ref claimed) != 0 || Volatile.Read(ref fallbackStarted) != 0)
      {
        return;
      }

      var sinceIdle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastIdleTicks));
      if (sinceIdle >= HostBusyTimeout && Interlocked.Exchange(ref claimed, 1) == 0)
      {
        done.TrySetException(new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          "Civil 3D is not processing requests: a dialog is open or a long operation is running. Close the dialog or wait, then retry. No drawing changes were made."));
        return;
      }

      if (mainWindow != IntPtr.Zero)
      {
        PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
      }
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

    try
    {
      await AwaitHostContextAsync(done.Task, cancellationToken);
    }
    finally
    {
      // Request finished, failed or was cancelled: nothing may run it later.
      Interlocked.Exchange(ref claimed, 1);
      CoreApp.Idle -= idle;
    }
  }

  [DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  // ---------------------------------------------------------------------------
  // Plugin-command route
  // ---------------------------------------------------------------------------

  // Work waiting for the next C3DMCPHOP run. Each item runs once, inside the
  // command, where the document is locked by the command and a regular
  // Transaction is recorded as one undo step.
  private static readonly ConcurrentQueue<Func<Task>> PluginCommandWork = new();

  internal const string PluginCommandName = "C3DMCPHOP";

  // Awaited by PluginEntry's C3DMCPHOP command method (async void, on the main
  // thread in command context), so the command stays active while the work
  // awaits Editor.CommandAsync.
  internal static async Task RunPluginCommandWorkAsync()
  {
    while (PluginCommandWork.TryDequeue(out var work))
    {
      try
      {
        await work();
      }
      catch (Exception ex)
      {
        PluginLog.Error("Host", "C3DMCPHOP work failed outside its own error handling.", ex);
      }
    }
  }

  // Main thread (an Idle tick). Queues the work and starts C3DMCPHOP with COM
  // SendCommand from a background thread: AutoCAD marshals that call to the
  // main thread like an external client's and runs it at once, which is the
  // route the command-context wedge leaves working. Completes when SendCommand
  // has returned; the work itself reports through the request's own task.
  private static async Task StartPluginCommandAsync(Func<Task> work, Func<bool> finished)
  {
    var doc = App.DocumentManager.MdiActiveDocument
      ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
    // The COM document must be fetched on the main thread.
    var acadDocument = doc.GetAcadDocument();
    PluginCommandWork.Enqueue(work);

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    await Task.Run(() =>
    {
      while (true)
      {
        try
        {
          Civil3DCompatibility.InvokeComMethod(acadDocument, "SendCommand", "_" + PluginCommandName + " ");
          return;
        }
        catch (COMException ex) when (IsCallRejected(ex) && DateTime.UtcNow < deadline)
        {
          Thread.Sleep(200);
        }
      }
    });

    if (!finished())
    {
      // SendCommand returned without the command running the work (it is
      // still queued, so drop it): report instead of waiting for the request
      // timeout.
      DropQueuedWork(work);
      throw new JsonRpcDispatchException(
        "CIVIL3D.HOST_BUSY",
        $"Civil 3D did not run the {PluginCommandName} command (another command or a dialog may be active). No drawing changes were made.");
    }
  }

  private static bool IsCallRejected(COMException ex) =>
    ex.HResult is unchecked((int)0x80010001) /* RPC_E_CALL_REJECTED */ or unchecked((int)0x8001010A) /* RPC_E_SERVERCALL_RETRYLATER */;

  private static void DropQueuedWork(Func<Task> work)
  {
    var keep = new List<Func<Task>>();
    while (PluginCommandWork.TryDequeue(out var queued))
    {
      if (!ReferenceEquals(queued, work)) keep.Add(queued);
    }

    foreach (var queued in keep)
    {
      PluginCommandWork.Enqueue(queued);
    }
  }

  // ExecuteInCommandContextAsync returns an awaitable ExecutionResult rather
  // than a Task; surface it as a Task so it can be raced against cancellation.
  private static async Task RunInCommandContextAsync(Func<object, Task> callback)
  {
    await App.DocumentManager.ExecuteInCommandContextAsync(callback, null);
  }

  // Runs the callback on the host's main thread via a one-shot Application.Idle
  // handler and completes when the async callback has finished, propagating any
  // exception it throws.
  //
  // Verified live on Civil 3D 2027 with zero documents open:
  // DocumentManager.ExecuteInApplicationContext never invoked its callback in
  // that state (the request timed out after 120 s with no document created),
  // whereas Application.Idle keeps firing while only the Start tab is showing.
  // Idle runs on the main thread, which is the context DocumentManager.Add needs.
  private static Task ExecuteInApplicationContextAsync(Func<object, Task> callback, CancellationToken cancellationToken)
  {
    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenRegistration cancellation = default;

    EventHandler? handler = null;
    handler = async (_, _) =>
    {
      // One-shot: detach before running so a slow callback cannot be re-entered
      // by the next idle tick.
      CoreApp.Idle -= handler;
      cancellation.Dispose();
      try
      {
        await callback(null!);
        completion.TrySetResult(true);
      }
      catch (Exception ex)
      {
        completion.TrySetException(ex);
      }
    };
    CoreApp.Idle += handler;

    // A request cancelled before the next idle tick (client disconnect while
    // Civil 3D is busy) must not leave its handler subscribed; otherwise
    // abandoned handlers accumulate until Idle next fires. Unsubscribe and
    // complete as cancelled so the awaiting request observes it promptly.
    cancellation = cancellationToken.Register(() =>
    {
      CoreApp.Idle -= handler;
      completion.TrySetCanceled(cancellationToken);
    });

    return completion.Task;
  }

  // Waits for a host-context hop without ignoring request cancellation. A
  // client disconnect cancels the request token (RpcTcpServer monitors the
  // socket); without this, a hop whose callback never fires would keep
  // HostExecutionGate held after the client has already gone away. If the
  // host callback does run later, it observes the cancelled token and does
  // no work; its result is discarded.
  private static async Task AwaitHostContextAsync(Task hostTask, CancellationToken cancellationToken)
  {
    if (hostTask.IsCompleted || !cancellationToken.CanBeCanceled)
    {
      await hostTask;
      return;
    }

    using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var abandonTask = Task.Delay(Timeout.Infinite, abandon.Token);
    var completed = await Task.WhenAny(hostTask, abandonTask);
    if (completed != hostTask)
    {
      cancellationToken.ThrowIfCancellationRequested();
    }

    // Release the abandon registration so it does not outlive the request.
    abandon.Cancel();
    await hostTask;
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<CancellationToken, Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      return await action(cancellationToken);
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }
}
