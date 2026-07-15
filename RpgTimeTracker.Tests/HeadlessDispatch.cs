using Avalonia.Headless;

namespace RpgTimeTracker.Tests;

/// <summary>
///     Runs a test body on a real (headless, windowless) Avalonia dispatcher loop, via the same
///     HeadlessUnitTestSession primitive Avalonia.Headless.XUnit's [AvaloniaFact] uses internally
///     - built directly instead of depending on that package because it pulls in xunit v3, which
///     conflicts with the classic xunit v2 this project otherwise uses (see the package reference
///     comment in RpgTimeTracker.Tests.csproj). Only MapFogIntegrationTests.cs needs this - see
///     its class-level doc comment for why.
/// </summary>
internal static class HeadlessDispatch
{
    public static async Task RunAsync(Func<Task> testBody)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessDispatch).Assembly);

        // HeadlessUnitTestSession.Dispatch has NO plain Func<Task> overload - only
        // Dispatch<TResult>(Func<TResult>, ...) and Dispatch<TResult>(Func<Task<TResult>>, ...).
        // Passing testBody (a Func<Task>) directly binds to the FIRST of those (TResult=Task):
        // it invokes testBody() as an ordinary synchronous function that happens to return a
        // Task VALUE, and never actually awaits that value - so anything in the test body after
        // its first "await" runs completely disconnected from the Task this method returns,
        // and any assertion failure there is silently lost instead of failing the test. Wrapping
        // in an explicitly-typed Func<Task<object?>> local (not an inline lambda passed straight
        // to Dispatch) forces the compiler to bind the SECOND overload instead, which correctly
        // awaits the inner task.
        Func<Task<object?>> wrapped = async () =>
        {
            await testBody().ConfigureAwait(false);
            return null;
        };

        await session.Dispatch(wrapped, CancellationToken.None).ConfigureAwait(false);
    }
}