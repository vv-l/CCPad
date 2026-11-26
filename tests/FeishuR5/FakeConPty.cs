namespace CCPad.Terminal;
internal sealed class ConPtySession
{
    public int Cols => 80;
    public int Rows => 24;
    public List<string> Writes { get; } = new();
    public event Action<byte[]>? OutputReceived;
    public void Emit(byte[] bytes) => OutputReceived?.Invoke(bytes);
    public void WriteInput(string text) => TryWriteInput(text);
    public bool TryWriteInput(string text) { Writes.Add(text); return true; }
    public void Resize(int cols, int rows) { }
}
