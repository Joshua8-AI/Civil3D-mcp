using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

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

      var hostTask = RunInCommandContextAsync(async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

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

        await Task.CompletedTask;
      });

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        throw capturedException;
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

      var hostTask = RunInCommandContextAsync(async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");

          using var documentLock = doc.LockDocument();
          result = action(doc, civilDoc, doc.Database);
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }

        await Task.CompletedTask;
      });

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        throw capturedException;
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
        throw capturedException;
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

      var hostTask = RunInCommandContextAsync(async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No commands were run.");
          }

          result = await action(doc, cancellationToken);
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      });

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        throw capturedException;
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
