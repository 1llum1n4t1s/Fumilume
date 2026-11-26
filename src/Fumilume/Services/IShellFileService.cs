namespace Fumilume.Services;

/// <summary>シェル操作の結果区分。</summary>
public enum ShellOperationOutcome
{
    /// <summary>要求した操作がすべて済んだ。</summary>
    Success,

    /// <summary>利用者が取り消した（進捗ダイアログの取り消しなど）。エラー表示は不要。</summary>
    Cancelled,

    /// <summary>失敗した（一部だけ済んだ場合を含む）。<see cref="ShellOperationResult.ErrorMessage"/> に理由を持つ。</summary>
    Failed,
}

/// <summary>
/// 実際に行われた 1 件の変更。<see cref="Source"/> は操作前のフルパス、<see cref="Destination"/> は操作後の
/// フルパス（自動改名が働いたときは改名後の実名）。ごみ箱への削除は <see cref="Destination"/> が空文字列、
/// 新規作成は <see cref="Source"/> と <see cref="Destination"/> がどちらも作成したパス。
/// </summary>
public readonly record struct ShellTransfer(string Source, string Destination);

/// <summary>
/// シェル操作の結果。例外は投げず、失敗は <see cref="ShellOperationOutcome.Failed"/> で返す。
/// <see cref="Completed"/> は <see cref="Outcome"/> に関係なく、実際に済んだ分だけを入れる
/// （途中で失敗・取り消しになっても、済んだ分はタブの付け替えなどに使う）。
/// </summary>
public sealed record ShellOperationResult(
    ShellOperationOutcome Outcome,
    IReadOnlyList<ShellTransfer> Completed,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Outcome == ShellOperationOutcome.Success;

    public bool IsCancelled => Outcome == ShellOperationOutcome.Cancelled;

    public bool IsFailed => Outcome == ShellOperationOutcome.Failed;

    public static ShellOperationResult Success(IReadOnlyList<ShellTransfer>? completed = null)
        => new(ShellOperationOutcome.Success, completed ?? []);

    public static ShellOperationResult Cancelled(IReadOnlyList<ShellTransfer>? completed = null)
        => new(ShellOperationOutcome.Cancelled, completed ?? []);

    public static ShellOperationResult Failed(string errorMessage, IReadOnlyList<ShellTransfer>? completed = null)
        => new(ShellOperationOutcome.Failed, completed ?? [], errorMessage);
}

/// <summary>
/// クリップボード上のファイル。<see cref="Token"/> は内容の世代を表す値で、
/// <see cref="IShellFileService.ClearFileClipboardIfUnchangedAsync"/> に渡して「自分が貼り付けた切り取りだけ」を消すのに使う。
/// </summary>
public sealed record FileClipboardContent(IReadOnlyList<string> Paths, bool IsCut, long Token);

/// <summary>
/// ファイル・フォルダに対するシェル操作の境界。Windows は IFileOperation・CF_HDROP、macOS は
/// NSFileManager・Avalonia の IClipboard などで実装し、既定は <see cref="UnavailableShellFileService"/>。
///
/// 共通の契約:
/// <list type="bullet">
/// <item>すべて UI スレッドから呼ぶ前提。実装は UI を塞がないよう内部で作業を移し、結果は UI スレッドの文脈へ戻す。
/// クリップボード系は macOS 実装が Avalonia の <c>IClipboard</c>（TopLevel 依存）を使うため、必ず非同期で呼び、UI スレッド以外から呼ばない。</item>
/// <item>失敗は例外ではなく <see cref="ShellOperationOutcome.Failed"/>（同期メソッドも <see cref="ShellOperationResult"/>）で返す。
/// 呼び出し側の取り消し・キャンセルと区別するため、利用者が取り消したときは <see cref="ShellOperationOutcome.Cancelled"/>。</item>
/// <item>パスはすべて絶対パス。<see cref="ShellOperationResult.Completed"/> は Outcome に関係なく、実際に済んだ分だけを返す。</item>
/// <item>実装は新しい名前（改名・作成）の検証をしない。<see cref="FileNameRules"/> での検証は呼び出し側が先に行う。</item>
/// </list>
/// </summary>
public interface IShellFileService
{
    /// <summary>「プロパティ」を出せるか（Windows のみ true）。false のときメニューに出さない。</summary>
    bool CanShowProperties { get; }

    /// <summary>
    /// 同じフォルダ内で名前だけを変える。<paramref name="newName"/> は区切りを含まない新しい名前（検証済み）。
    /// 大文字小文字だけの変更も受け付ける。変更先が既にあれば <see cref="ShellOperationOutcome.Failed"/>（上書きしない）。
    /// 成功時の <see cref="ShellOperationResult.Completed"/> は 1 件（元のパス, 新しいパス）。
    /// </summary>
    Task<ShellOperationResult> RenameAsync(string path, string newName);

    /// <summary>
    /// ごみ箱へ移す。Windows はごみ箱に送れない場所ではシェルが完全削除の確認を出し、利用者が承認した場合は完全削除になる
    /// （確認を省かないので黙っては消えない。Success でもごみ箱経由とは限らない）。
    /// macOS はごみ箱に入れられなければ <see cref="ShellOperationOutcome.Failed"/> を返し、完全削除へ切り替えない。
    /// 済んだ各項目は <see cref="ShellTransfer.Destination"/> が空文字列。
    /// </summary>
    Task<ShellOperationResult> MoveToTrashAsync(IReadOnlyList<string> paths);

    /// <summary>
    /// <paramref name="sources"/> を <paramref name="destinationFolder"/> へコピー（<paramref name="move"/> が false）または移動する。
    /// <paramref name="renameOnCollision"/> が true のときは、名前がぶつかる項目を自動改名して置く
    /// （同じフォルダへのコピーは「名前 のコピー」「名前 のコピー 2」のようになる。実名は <see cref="ShellOperationResult.Completed"/> の Destination）。
    /// false のとき、衝突の扱いは実装依存（Windows はシェルの確認ダイアログ、macOS は上書きせず飛ばす）。
    /// 飛ばした・失敗した項目は Completed に入らず、その場合の Outcome は Failed（ErrorMessage に件数などを書く）で、
    /// Success のときは必ず全項目が Completed に入る。済んだ分だけを返すので、呼び出し側は Outcome に関係なく Completed を見る。
    /// 移動先が移動元そのもの・配下のときは Failed（呼び出し側が事前に断ることも想定）。
    /// </summary>
    Task<ShellOperationResult> TransferAsync(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision);

    /// <summary>空のファイルを <paramref name="path"/> に作る（CreateNew 相当。既にあれば <see cref="ShellOperationOutcome.Failed"/>、上書きしない）。成功時の Completed は 1 件（path, path）。</summary>
    Task<ShellOperationResult> CreateFileAsync(string path);

    /// <summary>フォルダを <paramref name="path"/> に作る（既に同名のファイル・フォルダがあれば <see cref="ShellOperationOutcome.Failed"/>）。成功時の Completed は 1 件（path, path）。</summary>
    Task<ShellOperationResult> CreateFolderAsync(string path);

    /// <summary>ファイルとしてクリップボードへ入れる（<paramref name="isCut"/> が true なら切り取り）。入れられたら true、失敗は false。</summary>
    Task<bool> SetFileClipboardAsync(IReadOnlyList<string> paths, bool isCut);

    /// <summary>クリップボード上のファイルを返す。ファイルが無い・読めないときは null。</summary>
    Task<FileClipboardContent?> GetFileClipboardAsync();

    /// <summary>クリップボードにファイルがあるか。メニュー表示のたびに呼ぶので軽くする。</summary>
    Task<bool> HasFileClipboardAsync();

    /// <summary>
    /// クリップボードが <paramref name="token"/> の世代のままなら空にする（切り取りの貼り付け後に呼ぶ。
    /// その間に別の内容へ変わっていたら何もしない）。消したら true。
    /// </summary>
    Task<bool> ClearFileClipboardIfUnchangedAsync(long token);

    /// <summary>エクスプローラー・Finder で <paramref name="path"/> を選択状態で表示する。</summary>
    ShellOperationResult Reveal(string path);

    /// <summary>エクスプローラー・Finder で <paramref name="folder"/> の中身を開く（ツリーのフォルダの「エクスプローラーで開く」）。</summary>
    ShellOperationResult OpenFolder(string folder);

    /// <summary>ターミナルを <paramref name="folder"/> で開く（ファイルのときは呼び出し側が親フォルダを渡す）。</summary>
    ShellOperationResult OpenTerminal(string folder);

    /// <summary>プロパティ画面を出す。<see cref="CanShowProperties"/> が false のときは Failed。</summary>
    ShellOperationResult ShowProperties(string path);
}
