using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace BatteryLevelDisplay
{
    /// <summary>
    /// 售水机（vending_fountain）与自动贩卖机（vending_machine）的电量条：
    /// 这两类机器没有可见的物品格子（店门口是 StoreVendingFountain / StoreVendingMachine
    /// 的 SpriteRenderer 世界贴图），电量条画在贴图正上方。
    ///
    /// 实现：WorldSpace Canvas 直接用世界坐标锚定在 SpriteRenderer bounds 上方——
    /// 相机移动/缩放自动跟随，不存在屏幕坐标换算（此前换算出错把条画到了屏幕外）。
    /// 尺寸统一：Canvas 缩放按相机的"每像素世界单位"×窗口画布 scaleFactor 反算，
    /// 条高 11 画布单位在屏幕上的像素高度与物品格覆层完全一致，不随相机缩放变化。
    ///
    /// 拖放交换：店面机器没有窗口元素，原版拖拽目标系统（MayTarget/Target）不会为它
    /// 触发——由 Patch_EndDragPrefix 检测指针悬停在贴图上并完成交换（GetMachineUnderPointer）。
    ///
    /// 显示规则与普通机器一致：拖动电池 → 显示（含灰色无电池）；
    /// 红色告急 → 持续显示（0.5 秒轮询驱动）；松开电池 → 收起。
    /// </summary>
    internal static class WorldBatteryOverlay
    {
        private sealed class WorldEntry
        {
            internal Canvas canvas;      // WorldSpace Canvas（根级，DontDestroyOnLoad）
            internal TreeNodeRender root;
            internal TreeNodeRender fill;
            internal TreeNodeRender label;
            internal BatteryState lastState;
            internal float nextStateAt;
            internal bool visible;
        }

        private static readonly Dictionary<string, WorldEntry> Entries =
            new Dictionary<string, WorldEntry>();

        private static int _errorCount;
        private static bool _errorLogged;

        /// <summary>StartDrag/EndDrag postfix 与 0.5s 轮询共同调用。</summary>
        internal static void Sweep(bool forceState)
        {
            try
            {
                EmporiumEntry emporium = EmporiumEntry.Instance;

                SpriteRenderer vendingImage = StoreVendingMachine.Instance != null
                    ? StoreVendingMachine.Instance.image : null;
                SpriteRenderer fountainImage = StoreVendingFountain.Instance != null
                    ? StoreVendingFountain.Instance.image : null;

                SweepOne("vending_machine", vendingImage,
                    emporium != null ? emporium.vendingMachineElement : null, forceState);
                SweepOne("vending_fountain", fountainImage,
                    emporium != null ? emporium.vendingFountainElement : null, forceState);
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        /// <summary>
        /// 拖拽松手时，指针若悬在店面贴图上，返回对应的机器 GameItem（否则 null）。
        /// 卷闸门关闭（打烊）时返回 null——原版此时闸门挡住售货机、鼠标无法交互，
        /// 因此既不显示 tooltip 也不允许交换（StoreGlass.IsClosed，与原版同源）。
        /// </summary>
        internal static GameItem GetMachineUnderPointer()
        {
            if (StoreShutterClosed()) return null;

            Camera cam = Camera.main;
            if (cam == null) return null;

            Vector3 mouse = Input.mousePosition;
            EmporiumEntry emporium = EmporiumEntry.Instance;
            if (emporium == null) return null;

            GameItem m = OverStore(StoreVendingMachine.Instance != null ? StoreVendingMachine.Instance.image : null,
                emporium.vendingMachineElement, mouse, cam);
            if (m != null) return m;
            return OverStore(StoreVendingFountain.Instance != null ? StoreVendingFountain.Instance.image : null,
                emporium.vendingFountainElement, mouse, cam);
        }

        /// <summary>卷闸门是否已关闭（打烊状态）。判定与原版 StoreUIManager 同源。</summary>
        private static bool StoreShutterClosed()
        {
            try
            {
                StoreUIManager ui = StoreUIManager.Instance;
                return ui != null && ui.StoreGlass != null && ui.StoreGlass.IsClosed;
            }
            catch
            {
                return false;
            }
        }

        internal static void DestroyAll()
        {
            foreach (KeyValuePair<string, WorldEntry> kv in Entries)
            {
                if (kv.Value.canvas != null)
                {
                    UnityEngine.Object.Destroy(kv.Value.canvas.gameObject);
                }
            }
            Entries.Clear();
        }

        // ---------- 内部实现 ----------

        private static void SweepOne(string key, SpriteRenderer renderer,
            GameSlotInventory slot, bool forceState)
        {
            try
            {
                // 打烊卷闸门关闭：机器被闸门挡住、完全不可交互 → 电量条一并隐藏
                if (StoreShutterClosed())
                {
                    Hide(key);
                    return;
                }

                // 店面 Sprite 未激活 = 场景里没有该机器或未解锁
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    Hide(key);
                    return;
                }
                GameItem machine = slot != null ? slot.childItem : null;
                if (machine == null)
                {
                    Hide(key);
                    return;
                }

                WorldEntry entry;
                if (!Entries.TryGetValue(key, out entry))
                {
                    entry = new WorldEntry();
                    Entries.Add(key, entry);
                }

                // 状态读取节流
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
                    Hide(key);
                    return;
                }

                if (!BatteryOverlay.DraggingBattery()
                    && state.color != BatteryColor.Red && state.color != BatteryColor.Empty)
                {
                    Hide(key);
                    return;
                }

                Show(key, entry, state, renderer);
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        private static void Show(string key, WorldEntry entry, BatteryState state, SpriteRenderer renderer)
        {
            if (entry.canvas == null)
            {
                if (!CreateWorldCanvas(key, entry, renderer)) return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                Hide(key);
                return;
            }
            entry.canvas.worldCamera = cam;
            entry.canvas.gameObject.layer = renderer.gameObject.layer;

            Bounds b = renderer.bounds;

            // —— 尺寸统一 ——
            // 物品格覆层的条高是 11 画布单位 × 窗口画布 scaleFactor = 屏幕像素高度。
            // 世界空间按当前相机的"每像素世界单位"反算出相同的屏幕高度：
            // 条和文字（自动字号）在屏幕上与平面 UI 一致，相机缩放时保持不变。
            float uiScale = 1f;
            if (RenderHandler.current != null && RenderHandler.current.windowUICanvas != null)
            {
                uiScale = RenderHandler.current.windowUICanvas.scaleFactor;
            }
            float pixels = Mathf.Max(1f, Screen.height);
            float worldPerPixel;
            if (cam.orthographic)
            {
                worldPerPixel = cam.orthographicSize * 2f / pixels;
            }
            else
            {
                float dist = Vector3.Distance(cam.transform.position, b.center);
                worldPerPixel = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / pixels;
            }
            // 画布单位（条高 11）→ 世界尺寸：scale = 世界单位/画布单位
            float scale = Mathf.Max(0.0001f, uiScale * worldPerPixel);
            float barWUnits = Mathf.Max(10f, b.size.x * 0.92f / scale);

            RectTransform rt = entry.canvas.transform as RectTransform;
            rt.localScale = Vector3.one * scale;
            rt.sizeDelta = new Vector2(barWUnits, 11f);
            rt.position = new Vector3(b.center.x, b.max.y + 11f * scale * 0.55f, b.center.z);

            float w = barWUnits;
            RectTransform rrt = entry.root.rectTransform;
            rrt.anchorMin = Vector2.zero;
            rrt.anchorMax = Vector2.one;
            rrt.offsetMin = Vector2.zero;
            rrt.offsetMax = Vector2.zero;
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
                float frac = state.percent >= 0f ? Mathf.Clamp01(state.percent / 100f) : 1f;
                RectTransform frt = entry.fill.rectTransform;
                frt.anchorMin = Vector2.zero;
                frt.anchorMax = Vector2.zero;
                frt.pivot = Vector2.zero;
                frt.localScale = Vector3.one;
                frt.sizeDelta = new Vector2(Mathf.Max(2f, (w - 2f) * frac), Mathf.Max(1f, 9f));
                frt.anchoredPosition = new Vector2(1f, 1f);
                entry.fill.image.enabled = true;
                entry.fill.image.color = BatteryOverlay.ColorFor(state.color);
            }

            entry.label.text.text = BatteryOverlay.TextFor(state);
            // 实测定稿（轮换实验 A）：maskable 默认 + 自动字号(min7/max30)
            entry.label.text.maskable = true;
            entry.label.text.enableAutoSizing = true;
            entry.label.text.fontSizeMin = 7f;
            entry.label.text.fontSizeMax = 30f;

            entry.visible = true;
        }

        private static bool CreateWorldCanvas(string key, WorldEntry entry, SpriteRenderer renderer)
        {
            try
            {
                GameObject go = new GameObject("BatteryLevelWorldCanvas_" + key,
                    typeof(RectTransform), typeof(Canvas));
                UnityEngine.Object.DontDestroyOnLoad(go);
                Canvas canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = Camera.main;

                RectTransform canvasRT = canvas.transform as RectTransform;
                canvasRT.anchorMin = Vector2.one * 0.5f;
                canvasRT.anchorMax = Vector2.one * 0.5f;
                canvasRT.pivot = Vector2.one * 0.5f;
                canvasRT.localScale = Vector3.one * 0.3f;

                // root（黑底条）铺满画布
                TreeNodeRender root = TreeNodeRender.Instantiate();
                root.rectTransform.SetParent(canvasRT, false);
                root.gameObject.name = "BatteryLevelOverlayWorldRoot";
                root.image.sprite = null;
                root.image.raycastTarget = false;

                TreeNodeRender fill = TreeNodeRender.Instantiate();
                fill.SetParent(root);
                fill.gameObject.name = "BatteryLevelOverlayWorldFill";
                fill.image.sprite = null;
                fill.image.raycastTarget = false;

                TreeNodeRender label = TreeNodeRender.InstantiateText();
                label.SetParent(root);
                label.gameObject.name = "BatteryLevelOverlayWorldText";
                // 文字 prefab 的 TMP 组件默认禁用，创建时显式开启
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

                entry.canvas = canvas;
                entry.root = root;
                entry.fill = fill;
                entry.label = label;
                ModEntry.Log("[诊断][店面] " + key + ": 世界空间 Canvas 已创建");
                return true;
            }
            catch (Exception e)
            {
                ReportError(e);
                return false;
            }
        }

        private static GameItem OverStore(SpriteRenderer renderer, GameSlotInventory slot,
            Vector3 mouse, Camera cam)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                return null;
            }
            if (slot == null || slot.childItem == null) return null;

            Bounds b = renderer.bounds;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, 0f);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, 0f);
            Vector3[] corners =
            {
                new Vector3(b.min.x, b.min.y, b.min.z), new Vector3(b.max.x, b.min.y, b.min.z),
                new Vector3(b.min.x, b.max.y, b.min.z), new Vector3(b.max.x, b.max.y, b.min.z),
                new Vector3(b.min.x, b.min.y, b.max.z), new Vector3(b.max.x, b.min.y, b.max.z),
                new Vector3(b.min.x, b.max.y, b.max.z), new Vector3(b.max.x, b.max.y, b.max.z),
            };
            foreach (Vector3 c in corners)
            {
                Vector3 s = cam.WorldToScreenPoint(c);
                if (s.z <= 0f) return null; // 相机背后
                min = Vector3.Min(min, s);
                max = Vector3.Max(max, s);
            }

            const float slack = 4f;
            return mouse.x >= min.x - slack && mouse.x <= max.x + slack
                && mouse.y >= min.y - slack && mouse.y <= max.y + slack
                ? slot.childItem : null;
        }

        private static void Hide(string key)
        {
            WorldEntry entry;
            if (Entries.TryGetValue(key, out entry))
            {
                entry.visible = false;
                if (entry.root != null && entry.root.gameObject != null && entry.root.gameObject.activeSelf)
                {
                    entry.root.gameObject.SetActive(false);
                }
            }
        }

        private static void ReportError(Exception e)
        {
            _errorCount++;
            if (!_errorLogged)
            {
                _errorLogged = true;
                ModEntry.Err("店面机器电量条错误 (" + _errorCount + "): " + e);
            }
        }
    }
}
