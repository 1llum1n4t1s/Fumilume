using AvaloniaEdit.Document;

namespace Fumilume.ViewModels;

/// <summary>
/// sakura エディタのブックマーク（F_BOOKMARK_SET / NEXT / PREV / RESET / PATTERN）相当。
///
/// 印の持ち方は 2 つの要件を同時に満たす必要がある。
///
/// - 上に行を足しても印は同じ行に残る　→ 行番号では覚えられないので <see cref="TextAnchor"/> を使う
/// - 印を付けた行が消えれば印も消える　→ アンカーだけでは足りない
///
/// 後者が厄介で、行頭に置いたアンカーはその行を削除しても「削除範囲の境界」に居るため生き残り、
/// 次の行の頭へ滑ってしまう（<see cref="ILineTracker"/> で行の削除を追っても、AvaloniaEdit は
/// 行を消すときに前後の行オブジェクトを併合するので、どの行が消えたのかを素直には取れない）。
/// そこで、変更が起きる前に「削除範囲へ丸ごと収まる行」の印だけを自分で落としている。
/// </summary>
public sealed class DocumentBookmarks
{
    private readonly TextDocument _document;
    private readonly List<TextAnchor> _anchors = [];

    public DocumentBookmarks(TextDocument document)
    {
        _document = document;
        _document.Changing += OnDocumentChanging;
    }

    /// <summary>印の増減を知らせる。エディタの描画側がこれを見て引き直す。</summary>
    public event EventHandler? Changed;

    /// <summary>印の付いている行番号（昇順・重複なし）。</summary>
    public IReadOnlyList<int> Lines
    {
        get
        {
            Prune();
            return [.. _anchors.Select(anchor => anchor.Line).Distinct().Order()];
        }
    }

    public bool IsBookmarked(int lineNumber) => Lines.Contains(lineNumber);

    /// <summary>指定行の印を反転する。付けたときは <see langword="true"/>。</summary>
    public bool Toggle(int lineNumber)
    {
        Prune();
        var existing = _anchors.Where(anchor => anchor.Line == lineNumber).ToList();
        if (existing.Count > 0)
        {
            foreach (var anchor in existing)
            {
                _anchors.Remove(anchor);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        Add(lineNumber);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_anchors.Count == 0)
        {
            return;
        }

        _anchors.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>条件に合う行すべてへ印を付ける（sakura の「パターンに一致する行をマーク」相当）。</summary>
    public int MarkMatching(Func<string, bool> predicate)
    {
        var marked = 0;
        var alreadyMarked = Lines.ToHashSet();
        for (var number = 1; number <= _document.LineCount; number++)
        {
            var line = _document.GetLineByNumber(number);
            if (!predicate(_document.GetText(line.Offset, line.Length)) || alreadyMarked.Contains(number))
            {
                continue;
            }

            Add(number);
            marked++;
        }

        if (marked > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return marked;
    }

    /// <summary>指定行より後ろの最初の印。無ければ先頭へ回り込む（sakura と同じく巡回する）。</summary>
    public int? Next(int fromLine)
    {
        var lines = Lines;
        return lines.Count == 0 ? null : lines.FirstOrDefault(line => line > fromLine, lines[0]);
    }

    /// <summary>指定行より前の最後の印。無ければ末尾へ回り込む。</summary>
    public int? Previous(int fromLine)
    {
        var lines = Lines;
        return lines.Count == 0 ? null : lines.LastOrDefault(line => line < fromLine, lines[^1]);
    }

    private void Add(int lineNumber)
    {
        var clamped = Math.Clamp(lineNumber, 1, _document.LineCount);
        var anchor = _document.CreateAnchor(_document.GetLineByNumber(clamped).Offset);

        // 行頭の印は、その行頭へ文字を差し込んでも行に残ってほしい。
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        _anchors.Add(anchor);
    }

    /// <summary>削除で行ごと消える印を、変更が起きる前に落とす。</summary>
    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs args)
    {
        if (args.RemovalLength == 0 || _anchors.Count == 0)
        {
            return;
        }

        var start = args.Offset;
        var end = args.Offset + args.RemovalLength;
        var dropped = _anchors.RemoveAll(anchor =>
        {
            if (anchor.IsDeleted)
            {
                return true;
            }

            // 改行まで含めて削除範囲へ収まっている行だけを「消えた行」とみなす。
            var line = _document.GetLineByOffset(anchor.Offset);
            return line.Offset >= start && line.Offset + line.TotalLength <= end;
        });

        if (dropped > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Prune() => _anchors.RemoveAll(anchor => anchor.IsDeleted);
}
