using System;
using UnityEngine;

namespace BatteryLevelDisplay
{
    /// <summary>
    /// 「电池电量显示」模组入口。
    ///
    /// ModLoader 加载 DLL 时已对整个程序集 PatchAll，本入口不再自行调用 Harmony。
    /// 两个特性：
    ///   1. 拖动电池时，所有带电池槽的机器在格子上直接显示电量条，松手即与机内电池
    ///      交换（空槽则直接装入）——见 DragPatches.cs + BatteryOverlay.cs；
    ///   2. 机器电池进入红色状态（低电量 / 供不上电 / 无电池）→ 不拖电池也能直接在
    ///      机器上看到电量条。
    /// 覆层由 GameItemElement.ValidateSelect 的 postfix 驱动（原版渲染元素必调），
    /// 不依赖世界图根，任何场景中的机器都能覆盖；本入口不再需要 MonoBehaviour 宿主。
    /// </summary>
    public sealed class ModEntry : IMod
    {
        public const string MOD_ID = "battery_level_display";

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<bool> CfgVerbose;

        private static ModLog _log;
        private GameObject _host;

        public void Init(ModManifest manifest)
        {
            try { _log = new ModLog(manifest); } catch { _log = null; }

            ModConfig cfg = ModConfig.For(manifest != null && !string.IsNullOrEmpty(manifest.ID) ? manifest.ID : MOD_ID);

            CfgEnabled = cfg.Define("Enabled", true, "General",
                () => "启用电池电量显示",
                () => "拖动电池时所有机器显示电量条并可松手交换；机器电量告急时直接在机器上显示电量条。",
                false);

            // 红绿阈值不再开放配置：电量条颜色 = 原版电池贴图美术色，
            // 档位边界 = 贴图切换点（≥70 绿 / 30~69 橙 / 1~29 红 / 0 空），
            // 见 MachineBattery.GREEN_AT_PERCENT / ORANGE_AT_PERCENT。

            CfgVerbose = cfg.Define("Verbose", false, "Diagnostics",
                () => "详细日志",
                () => "打印交换过程日志，排查问题时开启。",
                false);

            cfg.Save();

            Log("电池电量显示 已加载 (Harmony 补丁由 ModLoader 自动应用)");
        }

        public void OnEnable()
        {
            // 定时刷新：ValidateSelect 只在库存/选中变化时触发，红色告急机器的持续
            // 显示必须不依赖拖电池——打开仓库等任何界面后 0.5 秒内自动出现。
            // （模组 DLL 里的 MonoBehaviour Update 模式已被多个模组验证可用）
            try
            {
                _host = new GameObject("BatteryLevelDisplayHost");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<BatteryOverlayRuntime>();
            }
            catch (Exception e)
            {
                Err("轮询宿主创建失败: " + e.Message);
            }
        }

        public void OnDisable()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }
            BatteryOverlay.DestroyAll();
            WorldBatteryOverlay.DestroyAll();
        }

        internal static void Log(string s) { try { _log?.Log(s); } catch { } }
        internal static void Warn(string s) { try { _log?.Warning(s); } catch { } }
        internal static void Err(string s) { try { _log?.Error(s); } catch { } }
        internal static bool Verbose => CfgVerbose != null && CfgVerbose.Value;
        internal static bool Enabled => CfgEnabled == null || CfgEnabled.Value;
    }

    /// <summary>每 0.5 秒扫一遍所有可见窗口里的电池机器，驱动红色告急的持续显示。</summary>
    public sealed class BatteryOverlayRuntime : MonoBehaviour
    {
        private const float INTERVAL = 0.5f;
        private float _timer;

        private void Update()
        {
            DragFeedback.Tick(); // 交换成功反馈 tooltip 到期收回
            _timer += Time.unscaledDeltaTime;
            if (_timer < INTERVAL) return;
            _timer = 0f;
            if (ModEntry.Enabled)
            {
                BatteryOverlay.SweepRenderedElements();
                WorldBatteryOverlay.Sweep(forceState: false);
            }
        }
    }
}
