using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Native;

namespace Srm.Runtime;

// tier2-guest-network-egress: WfpManagerが登録するallow/blockルールの条件に使う識別子。
// 従来はAppContainerパッケージSID（PackageSid）のみだったが、DC-016によりAppContainer
// トークン自体がBunの名前付きパイプ生成を壊すことが判明したため、AppContainerを経由しない
// ネスト実行向けにUserSid（制限付き専用アカウントのユーザーSID、restricted-account-app-isolation
// change依存）とAppPath（実行ファイルパス、子プロセスを生成しない単一プロセスアプリ向けの
// フォールバック）を追加する。
public enum WfpIdentityKind
{
    PackageSid,
    UserSid,
    AppPath,
}

public readonly struct WfpIdentity
{
    public WfpIdentityKind Kind { get; }
    public IntPtr Sid { get; }
    public string? ExecutablePath { get; }

    private WfpIdentity(WfpIdentityKind kind, IntPtr sid, string? executablePath)
    {
        Kind = kind;
        Sid = sid;
        ExecutablePath = executablePath;
    }

    public static WfpIdentity FromPackageSid(IntPtr sid) => new(WfpIdentityKind.PackageSid, sid, null);
    public static WfpIdentity FromUserSid(IntPtr sid) => new(WfpIdentityKind.UserSid, sid, null);
    public static WfpIdentity FromAppPath(string executablePath) => new(WfpIdentityKind.AppPath, IntPtr.Zero, executablePath);
}

public class WfpManager : IDisposable
{
    private IntPtr _engine = IntPtr.Zero;
    private readonly Guid _providerKey;
    private readonly Guid _subLayerKey;
    private bool _disposed;

    private WfpManager(string appName)
    {
        _providerKey = DeriveGuid($"SRM-Provider-{appName}");
        _subLayerKey = DeriveGuid($"SRM-SubLayer-{appName}");
    }

    // アプリ名からプロバイダー/サブレイヤーのGUIDを決定的に導出する。srm run（フィルターを
    // インストールするプロセス）とsrm stop（後から別プロセスとして起動し、片付けるプロセス）
    // は別々のプロセス実行なので、ランダムなGuid.NewGuid()では stop 側が同じキーを
    // 再現できない。MD5ハッシュの16バイトをそのままGUIDとして使う。
    private static Guid DeriveGuid(string seed) => new(MD5.HashData(Encoding.UTF8.GetBytes(seed)));

    public static WfpManager Install(NetworkPolicy network, WfpIdentity identity, string appName)
    {
        var mgr = new WfpManager(appName);

        var session = new WfpNative.FWPM_SESSION0
        {
            sessionKey = Guid.NewGuid(),
            displayData = new WfpNative.FWPM_DISPLAY_DATA0
            {
                name = $"SRM-{appName}",
                description = $"SRM network filter session for {appName}",
            },
            txnWaitTimeoutInMSec = 5000,
        };

        uint err = WfpNative.FwpmEngineOpen0(null, WfpNative.RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out mgr._engine);
        if (err != WfpNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"WFPエンジンのオープンに失敗しました (0x{err:X8})\n管理者権限で実行しているか確認してください");

        err = WfpNative.FwpmTransactionBegin0(mgr._engine, 0);
        if (err != WfpNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"WFPトランザクション開始に失敗しました (0x{err:X8})");

        // 識別子がAppPath/UserSidの場合のみ、FWPM_FILTER_CONDITION0が参照する補助データ
        // （FWP_BYTE_BLOB）を一度だけ解決し、複数のフィルター登録（BlockAll x2層 +
        // Allow x2層xホスト数）で使い回す。FwpmFilterAdd0は呼び出し時に条件データを
        // 内部にコピーするため、各Add呼び出し後に解放する必要はなく、トランザクション
        // 終了後にまとめて解放すればよい（既存のcondPtr/ipv6BufPtrの解放パターンと同じ
        // 考え方）。
        //
        // 【実機検証で判明した重要な否定的結果】FWPM_CONDITION_ALE_USER_IDは
        // FWPM_CONDITION_ALE_PACKAGE_IDと違い、生のSID（type=FWP_SID）を条件値として
        // 受け付けない。最初にFWP_SIDで試したところFwpmFilterAdd0が
        // FWP_E_TYPE_MISMATCH(0x80320027)で失敗することを実機で確認した。Microsoft公式
        // サンプル「Permitting and Blocking Applications and Users」の通り、対象SIDに
        // FWP_ACTRL_MATCH_FILTERアクセスを許可する自己相対セキュリティ記述子
        // （type=FWP_SECURITY_DESCRIPTOR_TYPE）を構築して渡す必要がある。
        var auxConditionBlob = IntPtr.Zero;
        var sdToFree = IntPtr.Zero;
        try
        {
            if (identity.Kind == WfpIdentityKind.AppPath)
            {
                err = WfpNative.FwpmGetAppIdFromFileName0(identity.ExecutablePath!, out auxConditionBlob);
                if (err != WfpNative.ERROR_SUCCESS)
                    throw new InvalidOperationException($"実行ファイルからのappId解決に失敗しました: {identity.ExecutablePath} (0x{err:X8})");
            }
            else if (identity.Kind == WfpIdentityKind.UserSid)
            {
                auxConditionBlob = BuildUserSidSecurityDescriptorBlob(identity.Sid, out sdToFree);
            }

            try
            {
                // 同名ポリシーの前回実行分（異常終了等で残った）が万が一残っていても
                // FwpmProviderAdd0/FwpmSubLayerAdd0がFWP_E_ALREADY_EXISTSで失敗しないよう、
                // 既存の同キーのプロバイダー/サブレイヤーを先にベストエフォートで削除しておく。
                RemoveFiltersForSubLayer(mgr._engine, mgr._subLayerKey);
                var subLayerKey = mgr._subLayerKey;
                var providerKey = mgr._providerKey;
                WfpNative.FwpmSubLayerDeleteByKey0(mgr._engine, ref subLayerKey);
                WfpNative.FwpmProviderDeleteByKey0(mgr._engine, ref providerKey);

                mgr.RegisterProvider(appName);
                mgr.RegisterSubLayer(appName);
                mgr.AddBlockAllRule(identity, auxConditionBlob);
                mgr.AddAllowRules(network.AllowHosts, identity, auxConditionBlob);

                err = WfpNative.FwpmTransactionCommit0(mgr._engine);
                if (err != WfpNative.ERROR_SUCCESS)
                    throw new InvalidOperationException($"WFPトランザクションのコミットに失敗しました (0x{err:X8})");
            }
            catch
            {
                WfpNative.FwpmTransactionAbort0(mgr._engine);
                throw;
            }
        }
        finally
        {
            if (identity.Kind == WfpIdentityKind.AppPath && auxConditionBlob != IntPtr.Zero)
                WfpNative.FwpmFreeMemory0(ref auxConditionBlob);
            if (identity.Kind == WfpIdentityKind.UserSid)
            {
                if (auxConditionBlob != IntPtr.Zero) Marshal.FreeHGlobal(auxConditionBlob);
                if (sdToFree != IntPtr.Zero) AclNative.LocalFree(sdToFree);
            }
        }

        return mgr;
    }

    // FWPM_CONDITION_ALE_USER_ID用の自己相対セキュリティ記述子を構築し、FWP_BYTE_BLOB
    // （size+dataポインタ）としてネイティブメモリに確保して返す。呼び出し元は返り値
    // （FWP_BYTE_BLOBラッパー）をMarshal.FreeHGlobalで、sdToFree（SD本体）を
    // AclNative.LocalFreeで、それぞれ解放する責任を持つ。
    private static IntPtr BuildUserSidSecurityDescriptorBlob(IntPtr userSid, out IntPtr sdToFree)
    {
        var access = new AclNative.EXPLICIT_ACCESS[]
        {
            new()
            {
                grfAccessPermissions = AclNative.FWP_ACTRL_MATCH_FILTER,
                grfAccessMode = AclNative.GRANT_ACCESS,
                grfInheritance = AclNative.NO_INHERITANCE,
                Trustee = new AclNative.TRUSTEE
                {
                    pMultipleTrustee = IntPtr.Zero,
                    MultipleTrusteeOperation = 0,
                    TrusteeForm = AclNative.TRUSTEE_FORM.TRUSTEE_IS_SID,
                    TrusteeType = AclNative.TRUSTEE_TYPE.TRUSTEE_IS_USER,
                    ptstrName = userSid,
                },
            },
        };

        uint err = AclNative.BuildSecurityDescriptorW(
            IntPtr.Zero, IntPtr.Zero, 1, access, 0, IntPtr.Zero, IntPtr.Zero, out var sdLen, out var sd);
        if (err != AclNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"ユーザーSID用セキュリティ記述子の構築に失敗しました (0x{err:X8})");

        sdToFree = sd;

        var blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WfpNative.FWP_BYTE_BLOB>());
        Marshal.StructureToPtr(new WfpNative.FWP_BYTE_BLOB { size = sdLen, data = sd }, blobPtr, false);
        return blobPtr;
    }

    // srm run とは別プロセスで起動される srm stop から呼ばれる。フィルターIDは
    // プロセスをまたいで持ち越せないため、決定的に再現したsubLayerKeyでシステム全体の
    // フィルターを列挙し、一致するものだけを削除する。
    public static void RemoveForApp(string appName)
    {
        var providerKey = DeriveGuid($"SRM-Provider-{appName}");
        var subLayerKey = DeriveGuid($"SRM-SubLayer-{appName}");

        var session = new WfpNative.FWPM_SESSION0
        {
            sessionKey = Guid.NewGuid(),
            displayData = new WfpNative.FWPM_DISPLAY_DATA0 { name = $"SRM-Cleanup-{appName}" },
            txnWaitTimeoutInMSec = 5000,
        };

        uint err = WfpNative.FwpmEngineOpen0(null, WfpNative.RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out var engine);
        if (err != WfpNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"WFPエンジンのオープンに失敗しました (0x{err:X8})\n管理者権限で実行しているか確認してください");

        try
        {
            WfpNative.FwpmTransactionBegin0(engine, 0);
            try
            {
                RemoveFiltersForSubLayer(engine, subLayerKey);
                WfpNative.FwpmSubLayerDeleteByKey0(engine, ref subLayerKey);
                WfpNative.FwpmProviderDeleteByKey0(engine, ref providerKey);
                WfpNative.FwpmTransactionCommit0(engine);
            }
            catch
            {
                WfpNative.FwpmTransactionAbort0(engine);
                throw;
            }
        }
        finally
        {
            WfpNative.FwpmEngineClose0(engine);
        }
    }

    private static void RemoveFiltersForSubLayer(IntPtr engine, Guid subLayerKey)
    {
        // 先に対象filterIdを全部集めてから、列挙を終えた後にまとめて削除する。
        // 列挙中に同じエンジンハンドルで削除を行うと、列挙カーソルの状態と
        // 干渉して一部が列挙から漏れる（実機で12件中4件しか消えない不具合を確認済み）。
        var idsToDelete = new List<ulong>();

        uint err = WfpNative.FwpmFilterCreateEnumHandle0(engine, IntPtr.Zero, out var enumHandle);
        if (err == WfpNative.ERROR_SUCCESS)
        {
            try
            {
                // システム全体には(Windows Defender等が登録する分も含め)1024を大きく超える
                // フィルターが存在しうるため、1回の列挙では全件を取得できない。
                // numEntriesReturned が要求数を下回る(=もう残りがない)までループする。
                const uint batchSize = 1024;
                while (true)
                {
                    err = WfpNative.FwpmFilterEnum0(engine, enumHandle, batchSize, out var entries, out var count);
                    if (err != WfpNative.ERROR_SUCCESS || count == 0) break;

                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var filterPtr = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                            var filter = Marshal.PtrToStructure<WfpNative.FWPM_FILTER0>(filterPtr);
                            if (filter.subLayerKey == subLayerKey)
                                idsToDelete.Add(filter.filterId);
                        }
                    }
                    finally
                    {
                        WfpNative.FwpmFreeMemory0(ref entries);
                    }

                    if (count < batchSize) break;
                }
            }
            finally
            {
                WfpNative.FwpmFilterDestroyEnumHandle0(engine, enumHandle);
            }
        }

        foreach (var id in idsToDelete)
            WfpNative.FwpmFilterDeleteById0(engine, id);
    }

    private void RegisterProvider(string appName)
    {
        var provider = new WfpNative.FWPM_PROVIDER0
        {
            providerKey = _providerKey,
            displayData = new WfpNative.FWPM_DISPLAY_DATA0
            {
                name = $"SRM-Provider-{appName}",
            },
        };
        uint err = WfpNative.FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero);
        if (err != WfpNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"WFPプロバイダーの登録に失敗しました (0x{err:X8})");
    }

    private void RegisterSubLayer(string appName)
    {
        var providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(_providerKey, providerKeyPtr, false);
            var subLayer = new WfpNative.FWPM_SUBLAYER0
            {
                subLayerKey = _subLayerKey,
                displayData = new WfpNative.FWPM_DISPLAY_DATA0
                {
                    name = $"SRM-SubLayer-{appName}",
                },
                providerKey = providerKeyPtr,
                weight = 0x8000,
            };
            uint err = WfpNative.FwpmSubLayerAdd0(_engine, ref subLayer, IntPtr.Zero);
            if (err != WfpNative.ERROR_SUCCESS)
                throw new InvalidOperationException($"WFPサブレイヤーの登録に失敗しました (0x{err:X8})");
        }
        finally
        {
            Marshal.FreeHGlobal(providerKeyPtr);
        }
    }

    private void AddBlockAllRule(WfpIdentity identity, IntPtr auxBlob)
    {
        foreach (var layer in new[] { WfpNative.FWPM_LAYER_ALE_AUTH_CONNECT_V4, WfpNative.FWPM_LAYER_ALE_AUTH_CONNECT_V6 })
        {
            var condition = BuildIdentityCondition(identity, auxBlob);
            var condPtr = Marshal.AllocHGlobal(Marshal.SizeOf(condition));
            try
            {
                Marshal.StructureToPtr(condition, condPtr, false);
                var filter = new WfpNative.FWPM_FILTER0
                {
                    filterKey = Guid.NewGuid(),
                    displayData = new WfpNative.FWPM_DISPLAY_DATA0 { name = "SRM-BlockAll" },
                    layerKey = layer,
                    subLayerKey = _subLayerKey,
                    // FWP_EMPTY = WFPにウェイトの自動算出を任せる。条件数が多いフィルターほど
                    // 自動的に高い実効ウェイトになるため、条件2つのAllowルール（SID+IP）は
                    // 条件1つのこのBlockAllルール（SIDのみ）より自動的に優先される。
                    weight = new WfpNative.FWP_VALUE0
                    {
                        type = WfpNative.FWP_DATA_TYPE.FWP_EMPTY,
                    },
                    numFilterConditions = 1,
                    filterCondition = condPtr,
                    action = new WfpNative.FWPM_ACTION0 { type = WfpNative.FWP_ACTION_BLOCK },
                };

                uint err = WfpNative.FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out _);
                if (err != WfpNative.ERROR_SUCCESS)
                    throw new InvalidOperationException($"ブロックルールの追加に失敗しました (0x{err:X8})");
            }
            finally
            {
                Marshal.FreeHGlobal(condPtr);
            }
        }
    }

    private void AddAllowRules(List<string> allowHosts, WfpIdentity identity, IntPtr auxBlob)
    {
        if (allowHosts.Count == 0) return;

        // ループバック(127.0.0.1 / ::1)は外部に一切出ない通信であり、allow_hostsで
        // 個別に許可されていなくても常に許可する。JavaのNIO Selector実装（Netty経由の
        // Minecraft/Forge等）はウェイクアップ機構として内部的にループバック接続を
        // 使用しており、これがブロックされると「Unable to establish loopback
        // connection」で起動時にクラッシュする（実機確認済み）。同様の理由で
        // ループバックに依存するツールは他にも多いと考えられるため、
        // allow_hostsが1件でも指定されている（＝ネットワーク利用を意図している）
        // ポリシーでは自動的に許可する。
        AddAllowIpRule(IPAddress.Loopback, identity, auxBlob);
        AddAllowIpRule(IPAddress.IPv6Loopback, identity, auxBlob);

        foreach (var host in allowHosts)
        {
            var ips = ResolveHost(host);
            foreach (var ip in ips)
                AddAllowIpRule(ip, identity, auxBlob);
        }
    }

    private void AddAllowIpRule(IPAddress ip, WfpIdentity identity, IntPtr auxBlob)
    {
        var layer = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? WfpNative.FWPM_LAYER_ALE_AUTH_CONNECT_V4
            : WfpNative.FWPM_LAYER_ALE_AUTH_CONNECT_V6;

        var identityCond = BuildIdentityCondition(identity, auxBlob);

        IntPtr ipv6BufPtr = IntPtr.Zero;
        try
        {
            var ipCond = BuildIpCondition(ip, out ipv6BufPtr);

            var conditions = new[] { identityCond, ipCond };
            var condPtr = Marshal.AllocHGlobal(Marshal.SizeOf(identityCond) * 2);
            try
            {
                Marshal.StructureToPtr(conditions[0], condPtr, false);
                Marshal.StructureToPtr(conditions[1], condPtr + Marshal.SizeOf(identityCond), false);

                var filter = new WfpNative.FWPM_FILTER0
                {
                    filterKey = Guid.NewGuid(),
                    displayData = new WfpNative.FWPM_DISPLAY_DATA0 { name = $"SRM-Allow-{ip}" },
                    layerKey = layer,
                    subLayerKey = _subLayerKey,
                    weight = new WfpNative.FWP_VALUE0
                    {
                        type = WfpNative.FWP_DATA_TYPE.FWP_EMPTY,
                    },
                    numFilterConditions = 2,
                    filterCondition = condPtr,
                    action = new WfpNative.FWPM_ACTION0 { type = WfpNative.FWP_ACTION_PERMIT },
                };

                uint err = WfpNative.FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out _);
                if (err != WfpNative.ERROR_SUCCESS)
                    throw new InvalidOperationException($"許可ルールの追加に失敗しました: {ip} (0x{err:X8})");
            }
            finally
            {
                Marshal.FreeHGlobal(condPtr);
            }
        }
        finally
        {
            if (ipv6BufPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(ipv6BufPtr);
        }
    }

    // 識別子の種類（PackageSid/UserSid/AppPath）に応じてWFP条件フィールドを切り替える。
    // AppPathの場合、auxBlobはInstall()が一度だけ解決した値をそのまま使う
    // （複数回のFwpmGetAppIdFromFileName0呼び出しを避けるため）。
    private static WfpNative.FWPM_FILTER_CONDITION0 BuildIdentityCondition(WfpIdentity identity, IntPtr auxBlob)
    {
        return identity.Kind switch
        {
            WfpIdentityKind.PackageSid => new WfpNative.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpNative.FWPM_CONDITION_ALE_PACKAGE_ID,
                matchType = WfpNative.FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new WfpNative.FWP_CONDITION_VALUE0
                {
                    type = WfpNative.FWP_DATA_TYPE.FWP_SID,
                    value = new WfpNative.FWP_VALUE_UNION { sd = identity.Sid },
                },
            },
            // FWPM_CONDITION_ALE_USER_IDはFWP_SIDを受け付けない（実機でFWP_E_TYPE_MISMATCH
            // 確認済み、Install()のコメント参照）。対象SIDにFWP_ACTRL_MATCH_FILTERを許可する
            // セキュリティ記述子（Install()がBuildUserSidSecurityDescriptorBlobで事前構築）を
            // FWP_SECURITY_DESCRIPTOR_TYPEとして渡す。
            WfpIdentityKind.UserSid => new WfpNative.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpNative.FWPM_CONDITION_ALE_USER_ID,
                matchType = WfpNative.FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new WfpNative.FWP_CONDITION_VALUE0
                {
                    type = WfpNative.FWP_DATA_TYPE.FWP_SECURITY_DESCRIPTOR_TYPE,
                    value = new WfpNative.FWP_VALUE_UNION { byteBlob = auxBlob },
                },
            },
            WfpIdentityKind.AppPath => new WfpNative.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpNative.FWPM_CONDITION_ALE_APP_ID,
                matchType = WfpNative.FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new WfpNative.FWP_CONDITION_VALUE0
                {
                    type = WfpNative.FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE,
                    value = new WfpNative.FWP_VALUE_UNION { byteBlob = auxBlob },
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        };
    }

    // IPv6の場合、FWPM_CONDITION_IP_REMOTE_ADDRESS は FWP_UINT32 ではなく
    // FWP_BYTE_ARRAY16_TYPE（16バイトの生アドレス、ネットワークバイトオーダー）を
    // 期待する。これを呼び出し元の固定型 FWP_UINT32/0 のまま登録すると
    // 「IP_REMOTE_ADDRESS == 0」という常に成立しない条件になり、AAAA解決された
    // 許可ホストへの接続がブロックされたままになる。
    private static WfpNative.FWPM_FILTER_CONDITION0 BuildIpCondition(IPAddress ip, out IntPtr ipv6BufPtr)
    {
        ipv6BufPtr = IntPtr.Zero;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var v6Bytes = ip.GetAddressBytes();
            ipv6BufPtr = Marshal.AllocHGlobal(16);
            Marshal.Copy(v6Bytes, 0, ipv6BufPtr, 16);

            return new WfpNative.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpNative.FWPM_CONDITION_IP_REMOTE_ADDRESS,
                matchType = WfpNative.FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new WfpNative.FWP_CONDITION_VALUE0
                {
                    type = WfpNative.FWP_DATA_TYPE.FWP_BYTE_ARRAY16_TYPE,
                    value = new WfpNative.FWP_VALUE_UNION { byteArray16 = ipv6BufPtr },
                },
            };
        }

        var bytes = ip.GetAddressBytes();
        uint ipVal = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(bytes, 0));

        return new WfpNative.FWPM_FILTER_CONDITION0
        {
            fieldKey = WfpNative.FWPM_CONDITION_IP_REMOTE_ADDRESS,
            matchType = WfpNative.FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
            conditionValue = new WfpNative.FWP_CONDITION_VALUE0
            {
                type = WfpNative.FWP_DATA_TYPE.FWP_UINT32,
                value = new WfpNative.FWP_VALUE_UNION { uint32 = ipVal },
            },
        };
    }

    private static IEnumerable<IPAddress> ResolveHost(string host)
    {
        // ワイルドカード（*.example.com）はv0.1では apex ドメインのみ解決
        var target = host.StartsWith("*.") ? host[2..] : host;
        try
        {
            return Dns.GetHostAddresses(target);
        }
        catch
        {
            return Enumerable.Empty<IPAddress>();
        }
    }

    // srm run はプロセスが起動して即リターンする設計であり、WFPフィルターは
    // 対象アプリが動いている間ずっと有効でなければならない。そのためDispose()では
    // フィルター自体を削除せず、単にこのプロセスが持つエンジンハンドルを閉じるだけに
    // とどめる（フィルター/サブレイヤー/プロバイダー自体はセッションが非dynamicなので
    // ハンドルを閉じても残り続ける）。実際の削除は RemoveForApp（srm stop経由）が行う。
    public void Dispose()
    {
        if (!_disposed && _engine != IntPtr.Zero)
        {
            WfpNative.FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
        _disposed = true;
    }
}
