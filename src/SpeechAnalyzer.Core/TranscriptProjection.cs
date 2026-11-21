using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SpeechAnalyzer.Core;

// Сохраняет объекты строк при финализации: привязки UI получают точечные уведомления.
public sealed class TranscriptProjection
{
    public ObservableCollection<TranscriptRow> Rows { get; } = [];
    public void Synchronize(IReadOnlyList<TranscriptSegment> segments)
    {
        var visible = segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToArray();
        var ids = visible.Select(s => s.Id).ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!ids.Contains(Rows[i].Id)) Rows.RemoveAt(i);
        var existing = Rows.ToDictionary(r => r.Id);
        for (int i = 0; i < visible.Length; i++)
        {
            var segment = visible[i];
            if (!existing.TryGetValue(segment.Id, out var row))
            {
                row = new(segment); Rows.Insert(i, row);
            }
            else
            {
                row.Update(segment);
                int previousIndex = Rows.IndexOf(row);
                if (previousIndex != i) Rows.Move(previousIndex, i);
            }
        }
    }
}

public sealed class TranscriptRow : INotifyPropertyChanged
{
    public Guid Id { get; }
    public string Header { get; private set; } = "";
    public string Text { get; private set; } = "";
    public bool IsFinal { get; private set; }
    public TranscriptRow(TranscriptSegment segment) { Id = segment.Id; Update(segment); }
    public void Update(TranscriptSegment segment)
    {
        if (segment.Id != Id) throw new ArgumentException("Нельзя заменить ID строки расшифровки.");
        string header = $"{TimeSpan.FromMilliseconds(segment.StartMs):hh\\:mm\\:ss\\.fff} · {(segment.Channel == AudioChannel.You ? "Вы" : "Собеседник")} · {(segment.IsFinal ? "окончательно" : "предварительно…")} · {segment.Id}";
        if (Header != header) { Header = header; Changed(nameof(Header)); }
        if (Text != segment.Text) { Text = segment.Text; Changed(nameof(Text)); }
        if (IsFinal != segment.IsFinal) { IsFinal = segment.IsFinal; Changed(nameof(IsFinal)); }
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
