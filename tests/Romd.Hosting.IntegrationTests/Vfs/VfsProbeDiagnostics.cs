using System.Text;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal sealed record VfsProbeDiagnosticCapture(string Text, long DroppedLines);

internal static class VfsProbeDiagnostics
{
    internal const int MaximumRetainedCharacters = 16 * 1024;

    public static async Task<VfsProbeDiagnosticCapture> DrainAsync(
        TextReader source, Func<string, string> redact, CancellationToken ct)
    {
        char[] buffer = new char[4096];
        var line = new StringBuilder(MaximumRetainedCharacters);
        var retained = new Queue<string>();
        int retainedLength = 0;
        long droppedLines = 0;
        bool oversized = false;

        void Retain(string text)
        {
            while (retainedLength + text.Length > MaximumRetainedCharacters)
                retainedLength -= retained.Dequeue().Length;
            retained.Enqueue(text);
            retainedLength += text.Length;
        }

        void FinishLine(bool terminated)
        {
            if (oversized)
                droppedLines++;
            else
            {
                if (terminated && line.Length > 0 && line[^1] == '\r')
                    line.Length--;
                string redacted;
                try
                { redacted = redact(line.ToString()); }
                catch (Exception error)
                {
                    droppedLines++;
                    Retain($"[redaction:{error.GetType().FullName}]\n");
                    line.Clear();
                    return;
                }
                if (redacted.Length > MaximumRetainedCharacters - (terminated ? 1 : 0))
                    droppedLines++;
                else
                    Retain(terminated ? redacted + "\n" : redacted);
            }
            line.Clear();
            oversized = false;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = await source.ReadAsync(buffer.AsMemory(), ct);
            ct.ThrowIfCancellationRequested();
            if (count == 0)
                break;
            for (int i = 0; i < count; i++)
            {
                char value = buffer[i];
                if (value == '\n')
                    FinishLine(terminated: true);
                else if (!oversized)
                {
                    if (line.Length == MaximumRetainedCharacters)
                    {
                        line.Clear();
                        oversized = true;
                    }
                    else
                        line.Append(value);
                }
            }
        }
        if (oversized || line.Length > 0)
            FinishLine(terminated: false);
        return new(string.Concat(retained), droppedLines);
    }
}
