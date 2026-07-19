using System.ComponentModel;
using System.Runtime.InteropServices;
using Srm.Runtime.Native;

namespace Srm.Runtime;

public class AppContainerSidFactory : IDisposable
{
    private IntPtr _sid = IntPtr.Zero;
    private bool _disposed;

    public IntPtr Sid
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sid;
        }
    }

    // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)
    private const int HResultAlreadyExists = unchecked((int)0x800700B7);

    public static AppContainerSidFactory Create(string appContainerName)
    {
        var factory = new AppContainerSidFactory();

        // DeriveAppContainerSidFromAppContainerNameはプロファイル名のハッシュからSIDを
        // 算出するだけの純粋関数で、実際にプロファイルが登録されているかは確認しない。
        // そのため必ずCreateAppContainerProfileを先に試み、プロファイル本体（%LOCALAPPDATA%
        // \Packages\<name>\ の作成やAppContainerとしての登録）を行う。既に存在する場合のみ
        // Deriveでそのプロファイルに対応するSIDを取得する。
        int hr = AppContainerNative.CreateAppContainerProfile(
            appContainerName,
            appContainerName,
            $"SRM AppContainer for {appContainerName}",
            IntPtr.Zero,
            0,
            out factory._sid);

        if (hr == HResultAlreadyExists)
        {
            hr = AppContainerNative.DeriveAppContainerSidFromAppContainerName(
                appContainerName, out factory._sid);
        }

        if (hr != 0 || factory._sid == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"AppContainerプロファイルの作成に失敗しました: {appContainerName} (HRESULT: 0x{hr:X8})\n" +
                "次を確認してください: 管理者権限で実行しているか、アプリ名に無効な文字が含まれていないか");

        return factory;
    }

    public void Dispose()
    {
        if (!_disposed && _sid != IntPtr.Zero)
        {
            AppContainerNative.FreeSid(_sid);
            _sid = IntPtr.Zero;
        }
        _disposed = true;
    }
}
