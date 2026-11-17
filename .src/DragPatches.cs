using System;
using HarmonyLib;
using UnityEngine;

namespace BatteryLevelDisplay
{
    /// <summary>
    /// 电量覆层驱动：原版每次校验/渲染元素都会调用 ValidateSelect
    /// （原版 mark/pin 覆层同样依赖它刷新），不需要自己轮询世界图根。
    /// </summary>
    [HarmonyPatch(typeof(GameItemElement), "ValidateSelect")]
    internal static class Patch_ValidateSelect
    {
        private static void Postfix(GameItemElement __instance)
        {
            if (!ModEntry.Enabled) return;
            BatteryOverlay.Refresh(__instance);
        }
    }

    /// <summary>
    /// 拿起电池的瞬间就显示全场电量覆层：ValidateSelect 拖拽期间不触发，
    /// 必须在 StartDrag 后主动扫描可见窗口（此时 isDragging 已为 true，
    /// Refresh 里 DraggingBattery() 成立 → 全部机器显示覆层）。
    /// </summary>
    [HarmonyPatch(typeof(ItemMouseDragHandler), "StartDrag")]
    internal static class Patch_StartDrag
    {
        private static void Postfix(ItemMouseDragHandler __instance)
        {
            try
            {
                DragFeedback.Cancel();
                if (!ModEntry.Enabled || !__instance.IsDraggingItem) return;
                GameItem dragged = __instance.GetCurrentItem();
                if (dragged == null || !MachineBattery.IsBattery(dragged)) return;
                BatteryOverlay.SweepRenderedElements(forceState: true);
                WorldBatteryOverlay.Sweep(forceState: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 松手后的交换结果反馈：原版 EndDrag 末尾会 HideTooltip，
    /// 所以反馈 tooltip 在 Postfix 补显，由轮询宿主 2.5 秒后收回。
    /// </summary>
    internal static class DragFeedback
    {
        private static readonly RichTextBuilder Builder = new RichTextBuilder(60);
        private static RichText _pending;
        private static float _hideAt = -1f;

        internal static void QueueSuccess(string prefix,
            string dragLabel, RenderHandler.ColorPalette dragPalette,
            string instLabel, RenderHandler.ColorPalette instPalette)
        {
            try
            {
                Builder.Clear();
                Builder.Add(prefix + " ", RenderHandler.ColorPalette.Cyan);
                Builder.Add(dragLabel, dragPalette);
                if (instLabel != null)
                {
                    Builder.Add(" <-> ", RenderHandler.ColorPalette.Cyan);
                    Builder.Add(instLabel, instPalette);
                }
                _pending = Builder.Build();
            }
            catch { _pending = null; }
        }

        /// <summary>EndDrag Postfix 调用：原版已收回 tooltip，这里补显反馈。</summary>
        internal static void Flush()
        {
            try
            {
                if (_pending == null || OverlayHandler.current == null) return;
                OverlayHandler.current.ShowTooltip(Input.mousePosition, Vector2.zero, _pending);
                _hideAt = Time.unscaledTime + 2.5f;
                _pending = null;
            }
            catch { _pending = null; }
        }

        /// <summary>轮询宿主每帧调用：到期收回反馈 tooltip。</summary>
        internal static void Tick()
        {
            if (_hideAt < 0f) return;
            if (Time.unscaledTime < _hideAt) return;
            _hideAt = -1f;
            try { OverlayHandler.current?.HideTooltip(); } catch { }
        }

        internal static void Cancel()
        {
            _pending = null;
            if (_hideAt < 0f) return;
            _hideAt = -1f;
            try { OverlayHandler.current?.HideTooltip(); } catch { }
        }
    }

    /// <summary>放下/取消电池后立即刷新：非红色覆层收起，红色保留（特性 2）。</summary>
    [HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
    internal static class Patch_EndDrag
    {
        /// <summary>
        /// 店面机器（售水机/自动贩卖机）的电池拖放：它们没有窗口元素，原版目标系统
        /// （MayTarget/Target）不会触发。在原版 EndDrag 之前检测指针是否悬在店面
        /// 贴图上——此时指针不在任何物品上，原版 EndDrag 为空操作（lastItem/lastInventory
        /// 均为 null），交换与原版 Target 流程完全同构，无冲突。
        /// </summary>
        private static void Prefix(ItemMouseDragHandler __instance)
        {
            try
            {
                if (!ModEntry.Enabled || !__instance.IsDraggingItem) return;
                GameItem dragged = __instance.GetCurrentItem();
                if (dragged == null || !MachineBattery.IsBattery(dragged)) return;

                GameItem machine = WorldBatteryOverlay.GetMachineUnderPointer();
                if (machine == null) return;
                if (!MachineBattery.MayDropOn(dragged, machine)) return;

                GameSlotInventory slot = MachineHelper.GetBatterySlot(machine);
                GameItem installed = slot != null ? slot.childItem : null;

                string dragLabel, instLabel = null;
                BatteryColor dragColor, instColor = BatteryColor.Gray;
                MachineBattery.DescribeSelf(dragged, out dragLabel, out dragColor);
                if (installed != null)
                    MachineBattery.DescribeSelf(installed, out instLabel, out instColor);

                if (installed != null)
                {
                    if (GraphUtils.SwapItemsInPlace(dragged, installed))
                    {
                        ModEntry.Log("店面机器已交换电池: " + machine.identifier);
                        DragFeedback.QueueSuccess("已交换电池",
                            dragLabel, MachineBattery.TooltipPalette(dragColor),
                            instLabel, MachineBattery.TooltipPalette(instColor));
                    }
                    else
                    {
                        ModEntry.Warn("店面机器电池交换未成功（SwapItemsInPlace 返回 false）");
                        DragFeedback.QueueSuccess("交换未成功:",
                            "拖拽的电池", RenderHandler.ColorPalette.Red, null, 0);
                    }
                }
                else
                {
                    GraphUtils.TryAcceptAll(slot, dragged, 1);
                    ModEntry.Log("已装入店面机器电池槽");
                    DragFeedback.QueueSuccess("已装入电池",
                        dragLabel, MachineBattery.TooltipPalette(dragColor), null, 0);
                }
            }
            catch (Exception e)
            {
                ModEntry.Err("店面机器电池交换失败: " + e.Message);
            }
        }

        private static void Postfix()
        {
            try
            {
                DragFeedback.Flush();
                if (!ModEntry.Enabled) return;
                BatteryOverlay.SweepRenderedElements(forceState: true);
                WorldBatteryOverlay.Sweep(forceState: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 特性 1 - 让"电池 → 带电池槽的机器"成为原版目标交互：
    /// MayTarget/CanTarget 放行后，原版 Drag 会给机器高亮绿色并显示目标提示，
    /// EndDrag 会调用 GameItem.Target —— 由 Patch_Target 完成交换。
    /// 拖动过程中机器的电量显示由 BatteryOverlay 负责（直接画在物品格子上，不是 tooltip）。
    /// </summary>
    [HarmonyPatch(typeof(GameItem), "MayTarget")]
    internal static class Patch_MayTarget
    {
        private static void Postfix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            if (__result || !ModEntry.Enabled) return;
            try
            {
                if (MachineBattery.MayDropOn(__instance, targetItem)) __result = true;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(GameItem), "CanTarget")]
    internal static class Patch_CanTarget
    {
        private static void Postfix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            if (__result || !ModEntry.Enabled) return;
            try
            {
                if (MachineBattery.MayDropOn(__instance, targetItem)) __result = true;
            }
            catch { }
        }
    }

    /// <summary>松手落点：机内有电池 → 整组交换；空槽 → 直接装入。</summary>
    [HarmonyPatch(typeof(GameItem), "Target")]
    internal static class Patch_Target
    {
        private static void Postfix(GameItem __instance, GameItem targetItem)
        {
            if (!ModEntry.Enabled) return;
            try
            {
                if (!MachineBattery.MayDropOn(__instance, targetItem)) return;

                GameSlotInventory slot = MachineHelper.GetBatterySlot(targetItem);
                GameItem installed = slot != null ? slot.childItem : null;

                string dragLabel, instLabel = null;
                BatteryColor dragColor, instColor = BatteryColor.Gray;
                MachineBattery.DescribeSelf(__instance, out dragLabel, out dragColor);
                if (installed != null)
                    MachineBattery.DescribeSelf(installed, out instLabel, out instColor);

                if (installed != null)
                {
                    if (GraphUtils.SwapItemsInPlace(__instance, installed))
                    {
                        if (ModEntry.Verbose)
                            ModEntry.Log("已交换电池: " + __instance.identifier + " <-> " + installed.identifier);
                        DragFeedback.QueueSuccess("已交换电池",
                            dragLabel, MachineBattery.TooltipPalette(dragColor),
                            instLabel, MachineBattery.TooltipPalette(instColor));
                    }
                    else
                    {
                        ModEntry.Warn("电池交换未成功（原版 SwapItemsInPlace 返回 false）");
                        DragFeedback.QueueSuccess("交换未成功:",
                            "拖拽的电池", RenderHandler.ColorPalette.Red, null, 0);
                    }
                }
                else
                {
                    GraphUtils.TryAcceptAll(slot, __instance, 1);
                    if (ModEntry.Verbose) ModEntry.Log("已装入电池: " + __instance.identifier);
                    DragFeedback.QueueSuccess("已装入电池",
                        dragLabel, MachineBattery.TooltipPalette(dragColor), null, 0);
                }
            }
            catch (Exception e)
            {
                ModEntry.Err("电池交换/装入失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 修正拖拽提示：拖着电池悬停机器时，原版"插入"分支优先于"目标"分支，
    /// 槽位被占时会显示误导性的"无法将能量电池放入XX！"。本补丁在原版 Drag
    /// 末尾运行，用电池交换提示覆盖本帧的原版 tooltip；离开机器时不做任何事。
    /// 另外店面机器（售水机/自动贩卖机）没有窗口元素、原版不会给它们显示任何
    /// tooltip——指针悬在店面贴图上时由本补丁补上同款提示。
    /// </summary>
    [HarmonyPatch(typeof(ItemMouseDragHandler), "Drag")]
    internal static class Patch_Drag
    {
        private static readonly RichTextBuilder Builder = new RichTextBuilder(60);

        private static void Postfix(ItemMouseDragHandler __instance)
        {
            if (!ModEntry.Enabled) return;
            try
            {
                if (!__instance.IsDraggingItem) return;
                GameItem dragged = __instance.GetCurrentItem();
                if (dragged == null || !MachineBattery.IsBattery(dragged)) return;

                // 店面机器没有物品元素，lastItem 不会指向它们：
                // 指针不在任何物品上时用贴图矩形命中检测补上（拖到店面贴图上同样有 tooltip）。
                GameItem hovered = LastHovered(__instance);
                if (hovered == null) hovered = WorldBatteryOverlay.GetMachineUnderPointer();
                if (hovered == null) return;

                // 拖拽期间每帧刷新店面覆层：世界空间 Canvas 的缩放按相机反算，
                // 相机缩放时条/文字的屏幕尺寸保持与平面 UI 一致（状态读取内部有 0.4s 节流）。
                WorldBatteryOverlay.Sweep(forceState: false);

                BatteryState state = MachineBattery.Read(hovered);
                if (state == null) return;

                Builder.Clear();
                Builder.AddLine(hovered.GetDisplayName(preferCustomName: true), false, RenderHandler.ColorPalette.Cyan);
                if (state.percent >= 0f)
                {
                    Builder.Add("电池 ", RenderHandler.ColorPalette.LightCyan);
                    Builder.AddProgressBar(state.percent / 100f, 10,
                        RenderHandler.ColorPalette.LightCyan, RenderHandler.ColorPalette.VeryDarkCyan, null, " ");
                    Builder.AddLine("(" + state.energyText + ")", true, RenderHandler.ColorPalette.LightCyan);
                }
                Builder.AddLine(state.headline, true, MachineBattery.TooltipPalette(state.color));

                // 交换提示带上双方电池的名字+电量，并按电量状态着色
                GameSlotInventory slot = MachineHelper.GetBatterySlot(hovered);
                GameItem installed = slot != null ? slot.childItem : null;

                string dragLabel;
                BatteryColor dragColor;
                MachineBattery.DescribeSelf(dragged, out dragLabel, out dragColor);
                Builder.Add("将", RenderHandler.ColorPalette.Cyan);
                Builder.Add(dragLabel, MachineBattery.TooltipPalette(dragColor));

                if (installed != null)
                {
                    string instLabel;
                    BatteryColor instColor;
                    MachineBattery.DescribeSelf(installed, out instLabel, out instColor);
                    Builder.Add(" 与机器内的 ", RenderHandler.ColorPalette.Cyan);
                    Builder.Add(instLabel, MachineBattery.TooltipPalette(instColor));
                    Builder.AddLine(" 进行交换", true, RenderHandler.ColorPalette.Cyan);
                }
                else
                {
                    Builder.AddLine(" 装入电池槽", true, RenderHandler.ColorPalette.Cyan);
                }

                // 与原版 Drag 相同的 tooltip 位置算法（tempShape 在 GameItemElement 上）
                float widthOffset = 0f;
                if (dragged is GameItemElement element)
                    widthOffset = element.tempShape.globalWidth * UISettings.current.gridSize / 2f;
                Vector2 local = __instance.tooltipTranslation + new Vector2(widthOffset, 0f);
                OverlayHandler.current.ShowTooltip(Input.mousePosition, local, Builder.Build());
            }
            catch (Exception e)
            {
                if (ModEntry.Verbose) ModEntry.Err("拖拽电量提示失败: " + e.Message);
            }
        }

        private static GameItem LastHovered(ItemMouseDragHandler handler)
        {
            try
            {
                return AccessTools.FieldRefAccess<ItemMouseDragHandler, GameItem>("lastItem")(handler);
            }
            catch
            {
                return null;
            }
        }
    }
}
