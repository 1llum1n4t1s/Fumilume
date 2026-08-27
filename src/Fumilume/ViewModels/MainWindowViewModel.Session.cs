using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// 前回終了時のワークスペースを控え、次回起動時に戻す面（メモ帳と同じ扱い）。
///
/// 「未保存でも確認せずに閉じられる」ことと「次に開いたとき同じ状態から続けられる」ことは同じ約束の
/// 表と裏なので、保存（<see cref="CaptureSession"/>）と復元（<see cref="RestoreSessionAsync"/>）を
/// ここへまとめてある。ディスクの読み書きそのものは <see cref="SessionStateService"/> の担当。
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>控えを読めなかった未保存タブの数。復元のあいだだけ数え、終わったら利用者へ知らせる。</summary>
    private int _sessionBuffersLost;

    /// <summary>前回終了時のタブを戻す。1 枚も戻せなければ何もしない（起動直後の空文書が残る）。</summary>
    private async Task RestoreSessionAsync(SessionState session)
    {
        if (session.Tabs.Count == 0)
        {
            return;
        }

        _sessionBuffersLost = 0;

        // コンストラクタが用意した空文書。復元できたぶんがあれば要らなくなる。
        var initial = Tabs.OfType<DocumentViewModel>().FirstOrDefault();

        WorkspaceTabViewModel? selected = null;
        for (var index = 0; index < session.Tabs.Count; index++)
        {
            var restored = await RestoreTabAsync(session.Tabs[index]);
            if (restored is null)
            {
                continue;
            }

            InsertContentTab(restored);
            if (index == session.SelectedTabIndex)
            {
                selected = restored;
            }
        }

        var restoredTabs = Tabs.Where(tab => !ReferenceEquals(tab, initial)).ToArray();
        if (restoredTabs.Length > 0)
        {
            if (initial is not null && IsPristine(initial))
            {
                DetachDocument(initial);
                Tabs.Remove(initial);
            }

            SelectedTab = selected ?? restoredTabs[0];

            // 復元した「無題 N」と同じ名前を次の新規文書へ振らないよう、採番を追い越させる。
            _untitledSequence = Tabs.OfType<DocumentViewModel>()
                .Select(document => ParseUntitledNumber(document.UntitledName))
                .DefaultIfEmpty(_untitledSequence)
                .Max();
        }

        // 控えを失ったときは、1 枚も戻せなかった場合でも黙って空の画面を出さない。
        if (_sessionBuffersLost > 0)
        {
            StatusMessage = $"未保存の内容を {_sessionBuffersLost:N0} 件復元できませんでした";
        }
        else if (restoredTabs.Length > 0)
        {
            StatusMessage = $"前回のタブを {restoredTabs.Length:N0} 件復元しました";
        }
    }

    private async Task<WorkspaceTabViewModel?> RestoreTabAsync(SessionTabState state)
    {
        try
        {
            return string.Equals(state.Kind, SessionTabKinds.Pdf, StringComparison.Ordinal)
                ? await RestorePdfTabAsync(state)
                : await RestoreDocumentTabAsync(state);
        }
        catch (Exception ex)
        {
            // 1 枚の失敗で残りの復元を止めない。
            AppLogger.For<MainWindowViewModel>().Warn(
                $"タブを復元できませんでした: {state.FilePath ?? state.UntitledName}",
                ex);
            return null;
        }
    }

    private async Task<DocumentViewModel?> RestoreDocumentTabAsync(SessionTabState state)
    {
        var document = new DocumentViewModel(state.UntitledName ?? UntitledNameFor(1), CloseTabCoreAsync);

        if (state.IsModified && state.Text is { } unsaved)
        {
            // 未保存の内容が正本。ディスクは読まない（読むと復元した内容を上書きしてしまう）。
            document.RestoreUnsaved(
                state.FilePath,
                new TextDocumentContent(unsaved, ParseEncoding(state.Encoding), state.NewLine));
        }
        else if (state.IsModified)
        {
            // 未保存だったのに控えが読めない（外部から消された・読み取りに失敗した）。
            // 保存済みファイルならディスクの内容で開き直すが、失われたことは黙らせない。
            // 控えの無い未保存の新規文書は中身が何も残っていないので、タブごと諦める。
            _sessionBuffersLost++;
            AppLogger.For<MainWindowViewModel>().Warn(
                $"未保存の内容を復元できませんでした: {state.FilePath ?? state.UntitledName}");
            if (state.FilePath is not { } lostPath || !File.Exists(lostPath))
            {
                return null;
            }

            document.Load(lostPath, await _files.ReadAsync(lostPath));
        }
        else if (state.FilePath is { } path)
        {
            // 未保存の変更が無いタブはディスクが正本。消えていればタブごと諦める。
            if (!File.Exists(path))
            {
                return null;
            }

            document.Load(path, await _files.ReadAsync(path));
        }

        ApplyDocumentViewState(document, state);
        document.PropertyChanged += OnDocumentPropertyChanged;
        return document;
    }

    private async Task<PdfDocumentViewModel?> RestorePdfTabAsync(SessionTabState state)
    {
        if (state.FilePath is not { } path || !File.Exists(path))
        {
            return null;
        }

        var pdf = await PdfDocumentViewModel.OpenAsync(path, CloseTabCoreAsync);
        await ApplyPdfViewStateAsync(pdf, state);
        return pdf;
    }

    /// <summary>
    /// 開いた PDF を控えの見え方（ページ・拡大率）へ合わせる。
    ///
    /// <see cref="PdfDocumentViewModel.OpenAsync"/> が描いたのは 1 ページ目・等倍で、ページ送りと
    /// 拡大のコマンドは「今と同じ値」なら早期に戻る。値を入れるだけでは画像が終了時の見え方に
    /// 追従しないので、最後に必ず描き直す。
    /// </summary>
    internal static async Task ApplyPdfViewStateAsync(PdfDocumentViewModel pdf, SessionTabState state)
    {
        if (state.PdfZoom > 0)
        {
            pdf.Zoom = Math.Clamp(state.PdfZoom, 0.25, 4.0);
        }

        if (state.PdfPage >= 1 && state.PdfPage <= pdf.PageCount)
        {
            pdf.CurrentPage = state.PdfPage;
        }

        await pdf.RenderCurrentPageAsync();
    }

    /// <summary>カーソル・選択・印・プレビュー状態を戻す。控えた位置は本文より長いことがあるので必ず丸める。</summary>
    private static void ApplyDocumentViewState(DocumentViewModel document, SessionTabState state)
    {
        var length = document.EditorDocument.TextLength;
        document.CaretIndex = Math.Clamp(state.CaretIndex, 0, length);

        var selectionStart = Math.Clamp(state.SelectionStart, 0, length);
        document.SelectionStart = selectionStart;
        document.SelectionLength = Math.Clamp(state.SelectionLength, 0, length - selectionStart);

        document.IsMarkdownPreview = state.IsMarkdownPreview && document.CanShowMarkdownPreview;

        var lineCount = document.EditorDocument.LineCount;
        foreach (var line in state.Bookmarks.Distinct().Where(line => line >= 1 && line <= lineCount))
        {
            document.Bookmarks.Toggle(line);
        }
    }

    /// <summary>今のワークスペースを控えの形にする。未保存の本文はここでだけ持ち出す。</summary>
    internal SessionState CaptureSession()
    {
        var session = new SessionState { SettingsTabOpen = SettingsTab is not null };

        foreach (var tab in Tabs)
        {
            var captured = tab switch
            {
                DocumentViewModel document => CaptureDocument(document),
                PdfDocumentViewModel pdf => CapturePdf(pdf),
                _ => null,
            };

            if (captured is null)
            {
                continue;
            }

            if (ReferenceEquals(tab, SelectedTab))
            {
                session.SelectedTabIndex = session.Tabs.Count;
            }

            session.Tabs.Add(captured);
        }

        return session;
    }

    private static SessionTabState CaptureDocument(DocumentViewModel document) => new()
    {
        Kind = SessionTabKinds.Document,
        FilePath = document.FilePath,
        UntitledName = document.UntitledName,
        IsModified = document.IsModified,
        Encoding = document.Encoding.ToString(),
        NewLine = document.NewLine,
        CaretIndex = document.CaretIndex,
        SelectionStart = document.SelectionStart,
        SelectionLength = document.SelectionLength,
        IsMarkdownPreview = document.IsMarkdownPreview,
        Bookmarks = document.HasBookmarks ? [.. document.Bookmarks.Lines] : [],
        // 本文を控えるのは未保存のときだけ。保存済みで変更が無ければディスクが正本。
        Text = document.IsModified ? document.Text : null,
    };

    private static SessionTabState CapturePdf(PdfDocumentViewModel pdf) => new()
    {
        Kind = SessionTabKinds.Pdf,
        FilePath = pdf.FilePath,
        PdfPage = pdf.CurrentPage,
        PdfZoom = pdf.Zoom,
    };

    private static DocumentEncoding ParseEncoding(string value)
        => Enum.TryParse<DocumentEncoding>(value, ignoreCase: true, out var parsed)
            ? parsed
            : DocumentEncoding.Utf8;

    /// <summary>「無題」なら 1、「無題 N」なら N。それ以外（保存済みファイル由来）は 0。</summary>
    private static int ParseUntitledNumber(string untitledName)
    {
        if (string.Equals(untitledName, UntitledNameFor(1), StringComparison.Ordinal))
        {
            return 1;
        }

        return untitledName.StartsWith(UntitledPrefix, StringComparison.Ordinal)
            && int.TryParse(untitledName.AsSpan(UntitledPrefix.Length), out var number)
                ? number
                : 0;
    }
}
