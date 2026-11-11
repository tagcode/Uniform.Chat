namespace Uniform.Matrix.Protocol.Core;

/// <summary>Text of exceptions for logs and messages.</summary>
public static class ExceptionText
{
    /// <summary>Return the messages of <paramref name="exception"/> and its inner exceptions, joined with arrows, e.g. <c>The SSL connection could not be established, see inner exception. → The remote certificate is invalid…</c>; a message an inner exception repeats is left out.</summary>
    public static string Describe(this Exception exception)
    {
        // Each message down the chain, once
        List<string> messages = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (messages.Count == 0 || messages[^1] != current.Message) messages.Add(current.Message);
        }
        // Return text
        return string.Join(" → ", messages);
    }
}
