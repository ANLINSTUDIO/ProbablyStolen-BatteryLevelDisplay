using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BatteryLevelDisplay
{
    /// <summary>
    /// 电量覆层：把电量条直接画在机器 GameItemElement 的格子上（不是 tooltip）。
    /// 特性 1 - 拖着电池时，所有带电池槽的机器都显示电量条（绿/橙/红按状态着色）；
    /// 特性 2 - 平时只有红色状态（电量 ≤ 红色阈值）的机器显示。
    /// 售水机/自动贩卖机的世界贴图覆层见 WorldBatteryOverlay.cs。
    ///
    /// 驱动方式（对齐已验证可行的"物品名称常显"模组）：
    /// GameItemElement.ValidateSelect 的 Harmony postfix —— 原版每次校验/渲染元素都会
    /// 调用它（原版 mark/pin 覆层同样依赖它刷新），因此不依赖 GameMaster/ship 图根，
    /// 商店、飞船、下层区任何图里的机器都能覆盖。
    ///
    /// 挂点与定位：节点挂在 element.handler 下（图标节点的父级，不随旋转变化），
    /// 用 element.background 的世界四角换算到 handler 本地坐标得到可见矩形，再按矩形
    /// 放置电量条。图标不在渲染（所在窗口关闭 / 元素被拖动显示 fake 镜像）时覆层跟随隐藏。
    /// </summary>
    internal static class BatteryOverlay
    {
        private const int CLEANUP_EVERY = 512; // 每 N 次 Refresh 清理一次已销毁元素的残留

        private sealed class Entry
        {
            internal TreeNodeRender root;
            internal TreeNodeRender fill;
            internal TreeNodeRender label;
            internal BatteryState lastState;      // 上次读取的状态
            internal float nextStateAt;           // 下次允许重新读取状态的时间（realtime）
            internal bool visible;                // 覆层当前是否激活
        }

        private static readonly Dictionary<GameItemElement, Entry> Cache =
            new Dictionary<GameItemElement, Entry>();

        private static readonly Vector3[] Corners = new Vector3[4];

        private static int _refreshCount;
        private static bool _firstShownLogged;
        private static int _errorCount;
        private static bool _errorLogged;

        /// <summary>ValidateSelect postfix 入口。必须便宜：非机器元素只做一次 tag 查表。</summary>
        internal static void Refresh(GameItemElement element, bool forceState = false)
        {
            if (element == null || element.isDebugMenu) return;

            if (++_refreshCount % CLEANUP_EVERY == 0) CleanupDead();

            try
            {
                if (element.handler == null)
                {
                    Remove(element);
                    return;
                }

                if (!element.IsTag("MACHINE_RECHARGABLE_TAG"))
                {
                    Remove(element);
                    return;
                }
                GameItem machine = element;

                // 图标没在渲染（格子所在窗口关闭 / 元素正被拖动显示 fake 镜像）→ 覆层跟随隐藏
                if (element.background == null || !element.background.image.enabled)
                {
                    Hide(element);
                    return;
                }

                Entry entry;
                if (!Cache.TryGetValue(element, out entry))
                {
                    entry = new Entry();
                    Cache.Add(element, entry);
                }

                // 状态读取节流（ValidateSelect 每帧都会来，电池数据不必每次重读）
                float now = Time.realtimeSinceStartup;
                BatteryState state = entry.lastState;
                if (state == null || forceState || now >= entry.nextStateAt)
                {
                    state = MachineBattery.Read(machine);
                    entry.lastState = state;
                    entry.nextStateAt = now + 0.4f;
                }

                if (state == null)
                {
                    Hide(element);
                    return;
                }

                // 特性 1：拖着电池 → 全显；特性 2：不拖 → 仅红色/耗尽状态显示
                if (!DraggingBattery() && state.color != BatteryColor.Red && state.color != BatteryColor.Empty)
                {
                    Hide(element);
                    return;
                }

                Rect bounds;
                if (!TryGetBounds(element, out bounds))
                {
                    Hide(element);
                    return;
                }

                Apply(element, entry, state, bounds);
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        internal static void DestroyAll()
        {
            List<GameItemElement> keys = new List<GameItemElement>(Cache.Keys);
            foreach (GameItemElement element in keys) Remove(element);
            Cache.Clear();
        }

        /// <summary>
        /// 拖拽开始/结束时由 StartDrag/EndDrag postfix 主动调用，0.5s 轮询也走这里：
        /// 枚举当前已渲染的所有物品元素（handler TreeNodeRender 的 .element 指向元素）。
        /// 注意不能用窗口图扫描——GameItemElement 是数据对象不是场景节点，
        /// GraphUtils.FindChildrenType 从 PixelWindow 出发遍历不到物品元素（实测失败）。
        /// 已渲染元素 = 打开的窗口里的物品；窗口关闭后节点失活，FindObjectsOfType
        /// 不再返回，对应覆层在下方"未见到即隐藏"逻辑里收起。
        /// </summary>
        internal static void SweepRenderedElements(bool forceState = false)
        {
            try
            {
                TreeNodeRender[] nodes = UnityEngine.Object.FindObjectsOfType<TreeNodeRender>();
                if (nodes == null) return;

                HashSet<GameItemElement> seen = new HashSet<GameItemElement>();
                foreach (TreeNodeRender node in nodes)
                {
                    if (node == null || node.gameObject == null) continue;
                    GameItemElement element = node.element as GameItemElement;
                    if (element == null || element.isDebugMenu) continue;
                    if (!element.IsTag("MACHINE_RECHARGABLE_TAG")) continue;

                    seen.Add(element);
                    Refresh(element, forceState);
                }

                // 缓存里存在但本轮没渲染到的元素（窗口已关闭）→ 收起覆层
                List<GameItemElement> stale = null;
                foreach (GameItemElement cached in Cache.Keys)
                {
                    if (!seen.Contains(cached)) (stale ?? new List<GameItemElement>()).Add(cached);
                }
                if (stale != null)
                {
                    foreach (GameItemElement element in stale) Hide(element);
                }
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        // ---------- 内部实现 ----------

        internal static bool DraggingBattery()
        {
            ItemMouseDragHandler drag = ItemMouseDragHandler.current;
            return drag != null && drag.IsDraggingItem && MachineBattery.IsBattery(drag.GetCurrentItem());
        }

        /// <summary>图标（background）四角 → handler 本地坐标 → 可见矩形。</summary>
        private static bool TryGetBounds(GameItemElement element, out Rect bounds)
        {
            bounds = default(Rect);
            RectTransform icon = element.background != null ? element.background.rectTransform : null;
            RectTransform space = element.handler != null ? element.handler.rectTransform : null;
            if (icon == null || space == null) return false;

            icon.GetWorldCorners(Corners);
            Vector2 min = Vector2.positiveInfinity;
            Vector2 max = Vector2.negativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector2 local = space.InverseTransformPoint(Corners[i]);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }

            float w = max.x - min.x;
            float h = max.y - min.y;
            if (w < 1f || h < 1f) return false;

            Vector2 spaceMin = space.rect.min;
            bounds = new Rect(min.x - spaceMin.x, min.y - spaceMin.y, w, h);
            return true;
        }

        private static void Apply(GameItemElement element, Entry entry, BatteryState state, Rect bounds)
        {
            if (entry.root == null || entry.fill == null || entry.label == null)
            {
                CreateNodes(element, entry);
                if (entry.root == null) return;
            }

            float w = Mathf.Max(bounds.width * 0.94f, 10f);
            // 实测：条高 <11 时 TMP 自动字号被压到下限 7，中文行高(≈1.48x)超出框体
            // 且被 6 层 RectMask2D 裁剪 → 文字整条消失；≥11.5（如净水器）则正常。
            float h = Mathf.Clamp(bounds.height * 0.18f, 11f, 16f);

            RectTransform rt = entry.root.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(
                bounds.xMin + (bounds.width - w) / 2f,
                bounds.yMax - h - Mathf.Max(1f, bounds.height * 0.04f));
            entry.root.image.enabled = true;
            entry.root.image.color = new Color(0.08f, 0.08f, 0.08f, 0.92f);
            entry.root.gameObject.SetActive(true);
            entry.root.rectTransform.SetAsLastSibling();

            if (state.color == BatteryColor.Gray || state.color == BatteryColor.Empty)
            {
                // 空电池槽 / 电量耗尽（battery0 贴图）：只显示黑底条，无前景填充
                entry.fill.image.enabled = false;
            }
            else
            {
                // 转换器 percent=-1 → frac=1，整条灰色填充，与空槽（黑底无填充）区分
                float frac = state.percent >= 0f ? Mathf.Clamp01(state.percent / 100f) : 1f;
                RectTransform frt = entry.fill.rectTransform;
                frt.anchorMin = Vector2.zero;
                frt.anchorMax = Vector2.zero;
                frt.pivot = Vector2.zero;
                frt.localScale = Vector3.one;
                frt.sizeDelta = new Vector2(Mathf.Max(2f, (w - 2f) * frac), Mathf.Max(1f, h - 2f));
                frt.anchoredPosition = new Vector2(1f, 1f);
                entry.fill.image.enabled = true;
                entry.fill.image.color = ColorFor(state.color);
            }

            entry.label.text.text = TextFor(state);
            // 实测定稿（轮换实验 A）：maskable 默认 + 自动字号(min7/max30)。
            // 实验结论：固定字号在 Truncate 模式下不渲染（B 失败），自动字号正常（A 成功）；
            // TMP 内部行为原因未明，以实测为准。用户确认 A 的字号观感最佳。
            TMP_Text lt = entry.label.text;
            lt.maskable = true;
            lt.enableAutoSizing = true;
            lt.fontSizeMin = 7f;
            lt.fontSizeMax = 30f;
            lt.overflowMode = TextOverflowModes.Truncate;

            entry.visible = true;

            if (!_firstShownLogged)
            {
                _firstShownLogged = true;
                ModEntry.Log("[诊断] 首次绘制电量条: '" + element.identifier + "' rect "
                    + bounds.width.ToString("0.#") + "x" + bounds.height.ToString("0.#")
                    + " activeInHierarchy=" + entry.root.gameObject.activeInHierarchy);
            }
        }

        private static void CreateNodes(GameItemElement element, Entry entry)
        {
            try
            {
                if (RenderHandler.current == null || element.handler == null) return;

                TreeNodeRender root = TreeNodeRender.Instantiate();
                root.SetParent(element.handler);
                root.gameObject.name = "BatteryLevelOverlay";
                root.image.sprite = null;
                root.image.raycastTarget = false;

                TreeNodeRender fill = TreeNodeRender.Instantiate();
                fill.SetParent(root);
                fill.gameObject.name = "BatteryLevelOverlayFill";
                fill.image.sprite = null;
                fill.image.raycastTarget = false;

                TreeNodeRender label = TreeNodeRender.InstantiateText();
                label.SetParent(root);
                label.gameObject.name = "BatteryLevelOverlayText";
                // 关键：文字 prefab 的 TMP 组件默认是禁用的，必须创建时显式开启
                //（参考模组 CreateText 同款；此前靠 Apply 每帧 enabled=true 无意续命，删掉后全灭）
                label.text.enabled = true;
                label.text.raycastTarget = false;
                label.text.richText = true;
                label.text.alignment = TextAlignmentOptions.Center;
                label.text.enableWordWrapping = false;
                label.text.overflowMode = TextOverflowModes.Truncate;
                label.text.fontStyle = FontStyles.Normal;
                label.text.color = Color.white;
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(1f, 0f);
                label.rectTransform.offsetMax = new Vector2(-1f, 0f);

                entry.root = root;
                entry.fill = fill;
                entry.label = label;
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        private static void Hide(GameItemElement element)
        {
            Entry entry;
            if (Cache.TryGetValue(element, out entry))
            {
                entry.visible = false;
                if (entry.root != null && entry.root.gameObject.activeSelf)
                {
                    entry.root.gameObject.SetActive(false);
                }
            }
        }

        private static void Remove(GameItemElement element)
        {
            Entry entry;
            if (!Cache.TryGetValue(element, out entry)) return;
            Cache.Remove(element);
            try
            {
                // fill/label 是 root 的子节点，随父节点一起销毁
                if (entry.root != null && entry.root.gameObject != null)
                    UnityEngine.Object.Destroy(entry.root.gameObject);
            }
            catch { }
        }

        private static void CleanupDead()
        {
            List<GameItemElement> dead = null;
            foreach (KeyValuePair<GameItemElement, Entry> kv in Cache)
            {
                GameItemElement key = kv.Key;
                if (key == null || (key.handler != null && IsDestroyed(key.handler)))
                {
                    (dead ?? new List<GameItemElement>()).Add(key);
                }
            }
            if (dead != null)
            {
                foreach (GameItemElement element in dead) Remove(element);
            }
        }

        private static bool IsDestroyed(TreeNodeRender node)
        {
            try { return node == null || node.gameObject == null; }
            catch { return true; }
        }

        /// <summary>
        /// 填充色 = 原版电池贴图的美术色（用户提供）：
        /// battery80/100 绿 #7AB053，battery40/60 橙 #D59F75，battery10/20 红 #9C3738。
        /// </summary>
        internal static Color ColorFor(BatteryColor color)
        {
            switch (color)
            {
                case BatteryColor.Green: return new Color(0.478f, 0.690f, 0.325f, 0.95f);   // #7AB053
                case BatteryColor.Orange: return new Color(0.835f, 0.624f, 0.459f, 0.95f);  // #D59F75
                case BatteryColor.Gray: return new Color(0.35f, 0.35f, 0.35f, 0.8f);
                // 转换器：偏蓝的特殊灰，与空槽的中性灰区分
                case BatteryColor.Converter: return new Color(0.45f, 0.52f, 0.62f, 0.9f);
                default: return new Color(0.612f, 0.216f, 0.220f, 0.95f);                    // #9C3738
            }
        }

        /// <summary>
        /// 电量条文本：精确电量（kJ）+ 每晚变化量（TMP 富文本三色）：
        /// 充/净增 → 绿 #abf575，净耗 → 红 #e15051，持平 → 灰 #c0c0c0。
        /// 变化量数值见 MachineBattery.ComputeNightDelta。
        /// </summary>
        internal static string TextFor(BatteryState state)
        {
            if (state.overlayText != null) return state.overlayText;
            if (state.percent < 0f) return "?";

            string t = state.valueText ?? Math.Round(state.percent) + "%";
            if (!state.hasDelta) return t;

            int d = state.deltaPerNight;
            if (d > 0) t += " <color=#abf575>+" + d + "</color>";
            else if (d < 0) t += " <color=#e15051>" + d + "</color>";
            else if (state.valueText != "0") t += " <color=#c0c0c0>+0</color>"; // 0 电量且无变化时不追加
            return t;
        }

        private static void ReportError(Exception e)
        {
            _errorCount++;
            if (!_errorLogged)
            {
                _errorLogged = true;
                ModEntry.Err("电量条错误 (" + _errorCount + "): " + e);
            }
        }
    }
}
