using System;

namespace BatteryLevelDisplay
{
    internal enum BatteryColor
    {
        Green,
        Orange,
        Red,
        /// <summary>电池槽为空：只显示黑底条，不触发告急的持续显示。</summary>
        Gray,
        /// <summary>电量为 0：对应原版 battery0 贴图（空电池外观），条上无填充。</summary>
        Empty,
        /// <summary>电流转换器：无限供电（开启时），特殊灰色，不触发告急显示。</summary>
        Converter
    }

    /// <summary>一台机器的电池状态快照（每次读取重新计算，不缓存）。</summary>
    internal sealed class BatteryState
    {
        internal BatteryColor color;
        /// <summary>电量百分比；-1 表示该电源类型没有百分比（超导电池/转换器/空槽）。</summary>
        internal float percent = -1f;
        internal string energyText;
        internal string headline;
        /// <summary>电量条文本：精确电量值（kJ），覆盖 percent 生成的默认文本。</summary>
        internal string overlayText;
        /// <summary>精确电量数值（kJ），电量条主文本。</summary>
        internal string valueText;
        /// <summary>每晚净电量变化量（自充 + 能量矩阵 − 机器耗电，含封顶）。</summary>
        internal int deltaPerNight;
        /// <summary>是否有每晚变化量可显示（空槽/超导/转换器为 false）。</summary>
        internal bool hasDelta;
    }

    internal static class MachineBattery
    {
        // 电量条颜色 = 原版电池贴图的颜色（PowerHelper.UpdateEnergyCreditSprite 的贴图
        // 切换档位 × 贴图美术色，游戏 0.46D）：
        //   档位：≥90→battery100 / ≥70→battery80 / ≥50→battery60 / ≥30→battery40 /
        //         ≥10→battery20 / ≥1→battery10 / 0→battery0
        //   贴图颜色：battery80/100 绿 #7AB053；battery40/60 橙 #D59F75；
        //             battery10/20 红 #9C3738；battery0 空（无填充）
        // 因此按贴图切换点分界：≥70 绿 / 30~69 橙 / 1~29 红 / 0 空。
        internal const float GREEN_AT_PERCENT = 70f;   // battery80 档起点
        internal const float ORANGE_AT_PERCENT = 30f;  // battery40 档起点

        internal static bool IsBattery(GameItem item)
        {
            return item != null && item.IsTag("power_source_item");
        }

        /// <summary>
        /// 读取机器的电池状态。非电池机器（无电池槽）返回 null。
        /// 电量来源（反编译确认，游戏 0.46D）：
        ///   - 普通电池：power_source_item_energy / power_source_item_max_energy（Tag，随存档持久化）；
        ///   - 超导电池：HypertechBatteryHelper，无储能，只看输出 kW 是否 ≥ 机器功耗；
        ///   - 电流转换器：无限供电，只看开/关。
        /// </summary>
        internal static BatteryState Read(GameItem machine)
        {
            GameSlotInventory slot = MachineHelper.GetBatterySlot(machine);
            if (slot == null) return null;

            GameItem battery = slot.childItem;
            int usage = MachineryHelper.GetMachinePowerUsage(machine);
            if (battery == null)
            {
                return new BatteryState
                {
                    color = BatteryColor.Gray,
                    headline = "电池槽为空",
                    overlayText = "空",
                };
            }

            if (HypertechBatteryHelper.IsHypertechBattery(battery))
            {
                int output = HypertechBatteryHelper.GetOutput(battery);
                bool ok = output >= usage;
                return new BatteryState
                {
                    color = ok ? BatteryColor.Green : BatteryColor.Red,
                    headline = ok
                        ? "超导电池：输出 " + output + " kW，可供电"
                        : "超导电池：输出 " + output + " kW，不够机器所需 " + usage + " kW",
                    overlayText = ok ? "超导" : "超导不足",
                };
            }

            if (CurrentConverterHelper.IsCurrentConverter(battery))
            {
                bool on = battery.GetTagReadonly("CURRENT_CONVERTER_ON_BOOL").GetBool();
                return new BatteryState
                {
                    color = on ? BatteryColor.Converter : BatteryColor.Red,
                    headline = on ? "电流转换器：开启（无限供电）" : "电流转换器：已关闭",
                    overlayText = on ? "已转换" : "转换器关",
                };
            }

            int energy = battery.GetTagReadonly("power_source_item_energy").GetInt();
            int max = battery.GetTagReadonly("power_source_item_max_energy").GetInt();
            if (max <= 0)
            {
                return new BatteryState
                {
                    color = BatteryColor.Red,
                    headline = "未知电源",
                    overlayText = "?",
                };
            }

            float pct = Math.Min(100f, Math.Max(0f, energy * 100f / max));

            BatteryColor color;
            string tail;
            if (pct < 1f)
            {
                color = BatteryColor.Empty;
                tail = " — 已耗尽";
            }
            else if (pct < ORANGE_AT_PERCENT)
            {
                color = BatteryColor.Red;
                tail = " — 电量告急";
            }
            else if (pct >= GREEN_AT_PERCENT)
            {
                color = BatteryColor.Green;
                tail = " — 可以供电";
            }
            else
            {
                color = BatteryColor.Orange;
                tail = " — 电量偏低";
            }

            return new BatteryState
            {
                color = color,
                percent = pct,
                valueText = energy.ToString(),
                energyText = energy + "/" + max + " kJ",
                headline = "电量 " + Math.Round(pct) + "%" + tail,
                deltaPerNight = ComputeNightDelta(machine, battery),
                hasDelta = true,
            };
        }

        /// <summary>
        /// 每晚净电量变化量（数值来源均为原版结算逻辑，游戏 0.46D）：
        ///   + 电池自我充能：power_source_item_recharge tag（每晚 ChargeBattery 应用）；
        ///   + 相邻能量矩阵（identifier "powerblock"）：每个每晚 +2，
        ///     购买王尔德"商用级供电单元"（NetworkUpgrade.IsCommercialPowerUnlocked）后 +4，
        ///     且仅商店通电（PlayerStore.isPowerOn）时充电；
        ///   − 机器每晚耗电：machinery_drawn_power（MachineryHelper.GetMachinePowerUsage）。
        /// 并按原版封顶规则折算为实际变化：充到 max 封顶、耗到 0 封顶。
        /// </summary>
        private static int ComputeNightDelta(GameItem machine, GameItem battery)
        {
            int max = battery.GetTagReadonly("power_source_item_max_energy").GetInt();
            if (max <= 0) return 0;
            int energy = battery.GetTagReadonly("power_source_item_energy").GetInt();

            int delta = 0;

            if (battery.IsTag("power_source_item_recharge"))
            {
                delta += battery.GetTagReadonly("power_source_item_recharge").GetInt();
            }

            if (PlayerStore.Instance != null && PlayerStore.Instance.isPowerOn)
            {
                int perBlock = NetworkUpgrade.IsCommercialPowerUnlocked() ? 4 : 2;
                foreach (GameItem adj in machine.FindAdjacentType<GameItem>())
                {
                    if (adj != null && adj.identifier == "powerblock") delta += perBlock;
                }
            }

            delta -= MachineryHelper.GetMachinePowerUsage(machine);

            if (delta > 0) delta = Math.Min(delta, max - energy);
            else if (delta < 0) delta = Math.Max(delta, -energy);
            return delta;
        }

        /// <summary>
        /// 电池物品自身的简短描述（tooltip 用），如 "能量电池 87%"。
        /// 与 Read 不同：Read 读的是"机器电池槽里的电池"，本方法直接描述一块电池物品
        /// （拖拽中的电池 / 机内电池本体）。
        /// </summary>
        internal static void DescribeSelf(GameItem battery, out string label, out BatteryColor color)
        {
            if (battery == null)
            {
                label = "空";
                color = BatteryColor.Gray;
                return;
            }

            string name = battery.GetDisplayName(preferCustomName: true);

            if (HypertechBatteryHelper.IsHypertechBattery(battery))
            {
                label = name + " " + HypertechBatteryHelper.GetOutput(battery) + "kW";
                color = BatteryColor.Green;
                return;
            }

            if (CurrentConverterHelper.IsCurrentConverter(battery))
            {
                bool on = battery.GetTagReadonly("CURRENT_CONVERTER_ON_BOOL").GetBool();
                label = name + (on ? " 已转换" : " 已关闭");
                color = on ? BatteryColor.Converter : BatteryColor.Red;
                return;
            }

            int energy = battery.GetTagReadonly("power_source_item_energy").GetInt();
            int max = battery.GetTagReadonly("power_source_item_max_energy").GetInt();
            if (max <= 0)
            {
                label = name;
                color = BatteryColor.Gray;
                return;
            }

            float pct = Math.Min(100f, Math.Max(0f, energy * 100f / max));
            label = name + " " + Math.Round(pct) + "%";
            color = pct < 1f ? BatteryColor.Empty
                  : pct < ORANGE_AT_PERCENT ? BatteryColor.Red
                  : pct >= GREEN_AT_PERCENT ? BatteryColor.Green
                  : BatteryColor.Orange;
        }

        /// <summary>电池状态 → tooltip 文字调色板（与电量条填充色一致的观感）。</summary>
        internal static RenderHandler.ColorPalette TooltipPalette(BatteryColor color)
        {
            switch (color)
            {
                case BatteryColor.Green: return RenderHandler.ColorPalette.LightGreen;
                case BatteryColor.Orange: return RenderHandler.ColorPalette.LightOrange;
                case BatteryColor.Gray: return RenderHandler.ColorPalette.Gray;
                case BatteryColor.Empty: return RenderHandler.ColorPalette.Gray;
                case BatteryColor.Converter: return RenderHandler.ColorPalette.LightTurquoise;
                default: return RenderHandler.ColorPalette.Red;
            }
        }

        /// <summary>
        /// 拖着电池悬停机器时，能否在该机器上松手（交换机内电池 / 装入空槽）。
        /// 除电池槽判断外，复刻原版 MayTarget/CanTarget 开头的归属权检查，
        /// 不绕过游戏"非自有物品不可交互"的规则。
        /// </summary>
        internal static bool MayDropOn(GameItem battery, GameItem machine)
        {
            if (battery == null || machine == null || battery == machine) return false;
            if (!IsBattery(battery)) return false;
            // SetupBatterySlot 会打上 MACHINE_RECHARGABLE_TAG，只认原版正规的机器电池槽
            if (!machine.IsTag("MACHINE_RECHARGABLE_TAG")) return false;
            if (battery.parentInventory == null) return false;
            if (!GeneralHelper.IsItemEffectivelyOwned(battery)) return false;
            if (!GeneralHelper.IsItemEffectivelyOwned(machine) && !battery.IsTag("CAN_USE_ON_NON_OWNED_TAG")) return false;

            GameSlotInventory slot = MachineHelper.GetBatterySlot(machine);
            if (slot == null || slot.childItem == battery) return false;
            try
            {
                if (slot.mayInventoryAddItemFunc != null && !slot.mayInventoryAddItemFunc(battery, slot)) return false;
            }
            catch
            {
                return false;
            }
            return true;
        }
    }
}
