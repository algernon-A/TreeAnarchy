﻿using ColossalFramework;
using ColossalFramework.IO;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using static TreeAnarchy.TAMod;

namespace TreeAnarchy {
    internal partial class TAPatcher : SingletonLite<TAPatcher> {
        private static IEnumerable<CodeInstruction> ReplaceLDCI4_MaxTreeLimit(IEnumerable<CodeInstruction> instructions) {
            bool foundTreeSig = false;
            MethodInfo get_TreeInstance = AccessTools.PropertyGetter(typeof(Singleton<TreeManager>), nameof(Singleton<TreeManager>.instance));
            foreach (var code in instructions) {
                if (code.Calls(get_TreeInstance)) {
                    foundTreeSig = true;
                    yield return code;
                } else if (foundTreeSig && code.Is(OpCodes.Ldc_I4, LastMaxTreeLimit)) {
                    yield return new CodeInstruction(OpCodes.Ldc_I4, MaxTreeLimit);
                } else {
                    yield return code;
                }
            }
        }

        // Patch WeatherManager::CalculateSelfHeight()
        // Affects Tree on Wind Effect, stops tree from slowing wind
        private static IEnumerable<CodeInstruction> CalculateSelfHeightTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            var codes = instructions.ToList();
            Label returnTreeManagerLabel = il.DefineLabel();
            LocalBuilder num2 = null, a = null; // local variables in WeatherManager::CalculateSelfHeight()
            int len = codes.Count - 1;
            MethodInfo getTreeInstance = AccessTools.PropertyGetter(typeof(Singleton<TreeManager>), nameof(Singleton<TreeManager>.instance));
            // extract two important variables
            for (int i = 0; i < len; i++) { // -1 since we will be checking i + 1
                if (codes[i].Calls(getTreeInstance)) {
                    // rewind and find num2 and a
                    int k = i - 10; // should be within 10 instructions
                    for (int j = i; j > k; j--) {
                        if (codes[j].opcode == OpCodes.Callvirt) {
                            num2 = codes[j - 2].operand as LocalBuilder;
                            a = codes[j - 1].operand as LocalBuilder;
                            break;
                        }
                    }
                    codes[i].labels.Add(returnTreeManagerLabel);
                    codes.InsertRange(i, new CodeInstruction[] {
                        /* The following instructions injects the following snippet into WeatherManager::CalculateSelfHeight()
                            if (TreeAnarchyConfig.TreeEffectOnWind)   //My Additions to overide tree effects.
                            {
                                return (ushort)Mathf.Clamp(num1 + num2 >> 1, 0, 65535);
                            }
                        */
                        new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(TAMod), nameof(TreeEffectOnWind))),
                        new CodeInstruction(OpCodes.Brfalse_S, returnTreeManagerLabel),
                        new CodeInstruction(OpCodes.Ldloc_S, num2),
                        new CodeInstruction(OpCodes.Ldloc_S, a),
                        new CodeInstruction(OpCodes.Add),
                        new CodeInstruction(OpCodes.Ldc_I4_1),
                        new CodeInstruction(OpCodes.Shr),
                        new CodeInstruction(OpCodes.Ldc_I4_0),
                        new CodeInstruction(OpCodes.Ldc_I4, 65535),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Mathf), "Clamp", new Type[] { typeof(int), typeof(int), typeof(int) })),
                        new CodeInstruction(OpCodes.Conv_U2),
                        new CodeInstruction(OpCodes.Ret)
                    });
                    break;
                }
            }

            return codes.AsEnumerable();
        }

        private const int MAX_MAPEDITOR_TREES = 250000;
        private const int MAX_MAP_TREES_CEILING = DefaultTreeLimit - 5;
        private static IEnumerable<CodeInstruction> CheckLimitsTranspiler(IEnumerable<CodeInstruction> instructions) {
            foreach (var instruction in instructions) {
                if (instruction.Is(OpCodes.Ldc_I4, MAX_MAPEDITOR_TREES))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(CheckLowLimit)));
                else if (instruction.Is(OpCodes.Ldc_I4, MAX_MAP_TREES_CEILING))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(CheckHighLimit)));
                else
                    yield return instruction;
            }
        }

        /* For Forestry Lock */
        private static IEnumerable<CodeInstruction> NRMTreesModifiedTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            Label jump = il.DefineLabel();
            var codes = ReplaceLDCI4_MaxTreeLimit(instructions).ToList();
            codes[0].WithLabels(jump);
            codes.InsertRange(0, new CodeInstruction[] {
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(TAMod), nameof(UseLockForestry))),
                new CodeInstruction(OpCodes.Brfalse_S, jump),
                new CodeInstruction(OpCodes.Ret)
            });
            return codes.AsEnumerable();
        }

        private static int treeCount = 0;
        public static void CustomSetPosY(TreeInstance[] trees, int treeID) {
            if ((trees[treeID].m_flags & 32) == 0) {
                trees[treeID].m_posY = 0;
            }
            treeCount++;
        }

        private static IEnumerable<CodeInstruction> DeserializeTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            bool firstSig = false, secondSig = false, thirdSig = false;
            MethodInfo integratedDeserialize = AccessTools.Method(typeof(TASerializableDataExtension), nameof(TASerializableDataExtension.IntegratedDeserialize));
            MethodInfo getTreeInstance = AccessTools.PropertyGetter(typeof(Singleton<TreeManager>), nameof(Singleton<TreeManager>.instance));
            MethodInfo getDataVersion = AccessTools.PropertyGetter(typeof(DataSerializer), nameof(DataSerializer.version));
            FieldInfo nextGridTree = AccessTools.Field(typeof(TreeInstance), nameof(TreeInstance.m_nextGridTree));
            var codes = instructions.ToList();
            int len = codes.Count;
            for (int i = 0; i < len; i++) {
                if (!firstSig && codes[i].Calls(getTreeInstance)) {
                    codes.InsertRange(i + 2, new CodeInstruction[] {
                        new CodeInstruction(OpCodes.Ldloc_0),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(TAPatcher.EnsureCapacity)))
                    });
                    firstSig = true;
                } else if (!secondSig && codes[i].opcode == OpCodes.Ldloc_1 && codes[i + 1].opcode == OpCodes.Ldlen && codes[i + 2].opcode == OpCodes.Conv_I4 && codes[i + 3].opcode == OpCodes.Stloc_3) {
                    codes.RemoveRange(i, 3);
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldc_I4, DefaultTreeLimit));
                    secondSig = true;
                } else if (!thirdSig && codes[i].Calls(getDataVersion)) {
                    while (++i < len) {
                        if (codes[i].opcode == OpCodes.Ldc_I4_1 && codes[i + 1].opcode == OpCodes.Stloc_S && codes[i + 2].opcode == OpCodes.Br) {
                            List<Label> labels = codes[i].labels;
                            CodeInstruction LdLoc_1 = new CodeInstruction(OpCodes.Ldloc_1).WithLabels(codes[i].labels);
                            codes[i] = new CodeInstruction(OpCodes.Ldc_I4_1);
                            codes.InsertRange(i, new CodeInstruction[] {
                                LdLoc_1,
                                new CodeInstruction(OpCodes.Call, integratedDeserialize),
                                new CodeInstruction(OpCodes.Ldloc_0),
                                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(TreeManager), nameof(TreeManager.m_trees))),
                                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Array32<TreeInstance>), nameof(Array32<TreeInstance>.m_buffer))),
                                new CodeInstruction(OpCodes.Stloc_1),
                                new CodeInstruction(OpCodes.Ldloc_1),
                                new CodeInstruction(OpCodes.Ldlen),
                                new CodeInstruction(OpCodes.Conv_I4),
                                new CodeInstruction(OpCodes.Stloc_3)
                            });
                            break;
                        }
                    }
                    for (i += 10; i < len; i++) {
                        if (codes[i].StoresField(nextGridTree)) {
                            codes.RemoveRange(i + 1, 5);
                            codes.InsertRange(i + 1, new CodeInstruction[] {
                                codes[i + 1],
                                codes[i + 2],
                                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(TAPatcher.CustomSetPosY)))
                            });
                            break;
                        }
                    }
                    thirdSig = true;
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        private const int KeepTree = 0;
        private const int RemoveTree = 1;
        private const int ReplaceTree = 2;
        private static void RemoveOrReplaceTree(uint treeID) {
            switch (RemoveReplaceOrKeep) {
            case RemoveTree:
                try {
                    Singleton<TreeManager>.instance.ReleaseTree(treeID);
                } catch {
                    TALog("Error occured releasing tree during prefab initialization");
                }
                break;
            case ReplaceTree:
                TreeInstance[] buffer = Singleton<TreeManager>.instance.m_trees.m_buffer;
                TreeInfo treeInfo = PrefabCollection<TreeInfo>.GetLoaded(0);
                buffer[treeID].Info = treeInfo;
                buffer[treeID].m_infoIndex = (ushort)treeInfo.m_prefabDataIndex;
                break;
            default:
                /* Keep missing tree */
                break;
            }
        }

        private static bool ValidateTreePrefab(TreeInfo treeInfo) {
            try {
                TreeInfo prefabInfo = PrefabCollection<TreeInfo>.GetLoaded((uint)treeInfo.m_prefabDataIndex);
                if (prefabInfo != null && prefabInfo.m_prefabDataIndex != -1) {
                    return true;
                }
            } catch {
                TALog("Exception occured during valiidate tree prefab. This is harmless");
            }
            return false;
        }

        public static bool OldAfterDeserializeHandler() {
            if (!OldFormatLoaded) return false;
            int maxLen = MaxTreeLimit;
            TreeInstance[] buffer = Singleton<TreeManager>.instance.m_trees.m_buffer;
            for (uint i = 1; i < maxLen; i++) {
                if (buffer[i].m_flags != 0) {
                    if (buffer[i].m_infoIndex >= 0) {
                        TreeInfo treeInfo = buffer[i].Info;
                        if (treeInfo == null || treeInfo?.m_prefabDataIndex < 0) {
                            RemoveOrReplaceTree(i);
                        } else {
                            if (ValidateTreePrefab(treeInfo)) {
                                buffer[i].m_infoIndex = (ushort)buffer[i].Info.m_prefabDataIndex;
                            } else {
                                RemoveOrReplaceTree(i);
                            }
                        }
                    }
                }
            }
            return true;
        }

        private static IEnumerable<CodeInstruction> AfterDeserializeTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            bool firstSig = false;
            bool secondSig = false;
            Label isOldFormatExit = il.DefineLabel();
            var codes = instructions.ToList();
            int len = codes.Count;
            for (int i = 0; i < len; i++) {
                if (!firstSig && codes[i].opcode == OpCodes.Ldc_I4_1 && codes[i + 2].opcode == OpCodes.Br) {
                    codes.InsertRange(i, new CodeInstruction[] {
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(TAPatcher.OldAfterDeserializeHandler))),
                        new CodeInstruction(OpCodes.Brtrue, isOldFormatExit)
                    });
                    firstSig = true;
                } else if (!secondSig && codes[i].opcode == OpCodes.Ldloc_0 && codes[i + 1].opcode == OpCodes.Ldloc_0) {
                    codes[i].WithLabels(isOldFormatExit);
                    secondSig = true;
                    break;
                }
            }

            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> SerializeTranspiler(IEnumerable<CodeInstruction> instructions) {
            bool sigFound = false;
            int firstIndex = -1, lastIndex = -1;
            var codes = instructions.ToList();
            int len = codes.Count;
            FieldInfo burningTrees = AccessTools.Field(typeof(TreeManager), nameof(TreeManager.m_burningTrees));
            MethodInfo loadingManagerInstance = AccessTools.PropertyGetter(typeof(Singleton<LoadingManager>), nameof(Singleton<LoadingManager>.instance));
            for (int i = 0; i < len; i++) {
                if (codes[i].opcode == OpCodes.Stloc_2 && !sigFound) {
                    int index = i - 3;
                    codes.RemoveRange(index, 3);
                    codes.Insert(index, new CodeInstruction(OpCodes.Ldc_I4, DefaultTreeLimit));
                    sigFound = true;
                } else if (codes[i].LoadsField(burningTrees) && firstIndex < 0) {
                    firstIndex = i - 1;
                } else if (codes[i].Calls(loadingManagerInstance) && lastIndex < 0 && sigFound) {
                    lastIndex = i;
                    break;
                }
            }
            codes.RemoveRange(firstIndex, lastIndex - firstIndex);
            codes.InsertRange(firstIndex, new CodeInstruction[] {
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(DataSerializer), nameof(DataSerializer.WriteUInt24)))
            });

            return codes.AsEnumerable();
        }

        private static IEnumerable<CodeInstruction> AwakeTranspiler(IEnumerable<CodeInstruction> instructions) {
            foreach (var code in instructions) {
                if (code.LoadsConstant(DefaultTreeLimit)) {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(MaxTreeLimit)));
                } else if (code.LoadsConstant(DefaultTreeUpdateCount)) {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(MaxTreeUpdateLimit)));
                } else yield return code;
            }
        }

        public static int BeginRenderSkipCount = 0;
        private static IEnumerable<CodeInstruction> BeginRenderingImplTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            Label CountExpired = il.DefineLabel();
            var codes = instructions.ToList();

            codes[0].WithLabels(CountExpired);
            codes.InsertRange(0, new CodeInstruction[] {
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(TAPatcher), nameof(BeginRenderSkipCount))),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Ldc_I4_1),
                new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Stsfld, AccessTools.Field(typeof(TAPatcher), nameof(BeginRenderSkipCount))),
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(TAMod), nameof(BeginSkipFrameCount))),
                new CodeInstruction(OpCodes.Rem),
                new CodeInstruction(OpCodes.Brfalse_S, CountExpired),
                new CodeInstruction(OpCodes.Ret),
            });

            return codes.AsEnumerable();
        }

        internal void InjectTreeLimit(Harmony harmony) {
            HarmonyMethod replaceLDCI4 = new(AccessTools.Method(typeof(TAPatcher), nameof(ReplaceLDCI4_MaxTreeLimit)));
            harmony.Patch(AccessTools.Method(typeof(BuildingDecoration), nameof(BuildingDecoration.SaveProps)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(BuildingDecoration), nameof(BuildingDecoration.ClearDecorations)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(CommonBuildingAI), @"HandleFireSpread"), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(DisasterHelpers), nameof(DisasterHelpers.DestroyTrees)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(FireCopterAI), @"FindBurningTree"), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(ForestFireAI), @"FindClosestTree"), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(NaturalResourceManager), nameof(NaturalResourceManager.TreesModified)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeTool), @"ApplyBrush"), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.AfterTerrainUpdate)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CalculateAreaHeight)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CalculateGroupData)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), @"EndRenderingImpl"), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), @"HandleFireSpread"), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.OverlapQuad)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.PopulateGroupData)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.RayCast)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.SampleSmoothHeight)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.TerrainUpdated)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.UpdateData)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.UpdateTrees)), transpiler: replaceLDCI4);
            harmony.Patch(AccessTools.Method(typeof(NaturalResourceManager), nameof(NaturalResourceManager.TreesModified)),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(NRMTreesModifiedTranspiler))));
            harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CheckLimits)),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(CheckLimitsTranspiler))));
        }

        internal void RemoveTreeLimitPatches(Harmony harmony) {
            harmony.Unpatch(AccessTools.Method(typeof(BuildingDecoration), nameof(BuildingDecoration.SaveProps)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(BuildingDecoration), nameof(BuildingDecoration.ClearDecorations)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(CommonBuildingAI), @"HandleFireSpread"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(DisasterHelpers), nameof(DisasterHelpers.DestroyTrees)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(FireCopterAI), @"FindBurningTree"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(ForestFireAI), @"FindClosestTree"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(NaturalResourceManager), nameof(NaturalResourceManager.TreesModified)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeTool), @"ApplyBrush"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.AfterTerrainUpdate)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CalculateAreaHeight)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CalculateGroupData)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), @"EndRenderingImpl"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), @"HandleFireSpread"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.OverlapQuad)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.PopulateGroupData)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.RayCast)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.SampleSmoothHeight)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.TerrainUpdated)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.UpdateData)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.UpdateTrees)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(NaturalResourceManager), nameof(NaturalResourceManager.TreesModified)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CheckLimits)), HarmonyPatchType.Transpiler, HARMONYID);
        }

        private void EnableTreeLimitPatches(Harmony harmony) {
            try {
                InjectTreeLimit(harmony);
                harmony.Patch(AccessTools.Method(typeof(TreeManager), @"Awake"),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(AwakeTranspiler))));
                harmony.Patch(AccessTools.Method(typeof(WeatherManager), @"CalculateSelfHeight"),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(CalculateSelfHeightTranspiler))));
                harmony.Patch(AccessTools.Method(typeof(TreeManager.Data), nameof(TreeManager.Data.Deserialize)),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(DeserializeTranspiler))));
                harmony.Patch(AccessTools.Method(typeof(TreeManager.Data), nameof(TreeManager.Data.Serialize)),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(SerializeTranspiler))));
                harmony.Patch(AccessTools.Method(typeof(TreeManager.Data), nameof(TreeManager.Data.AfterDeserialize)),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(AfterDeserializeTranspiler))));
                harmony.Patch(AccessTools.Method(typeof(TreeManager), "BeginRenderingImpl"),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(BeginRenderingImplTranspiler))));
            } catch (Exception e) {
                Debug.LogException(e);
            }
        }

        private void DisableTreeLimitPatches(Harmony harmony) {
            RemoveTreeLimitPatches(harmony);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), @"Awake"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(WeatherManager), @"CalculateSelfHeight"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager.Data), nameof(TreeManager.Data.Deserialize)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager.Data), nameof(TreeManager.Data.Serialize)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager.Data), nameof(TreeManager.Data.AfterDeserialize)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeManager), "BeginRenderingImpl"), HarmonyPatchType.Transpiler, HARMONYID);
        }

        public static void EnsureCapacity(TreeManager manager) {
            if (manager.m_trees.m_buffer.Length != MaxTreeLimit) {
                manager.m_trees = new Array32<TreeInstance>((uint)MaxTreeLimit);
                manager.m_updatedTrees = new ulong[MaxTreeUpdateLimit];
                Array.Clear(manager.m_trees.m_buffer, 0, manager.m_trees.m_buffer.Length);
                manager.m_trees.CreateItem(out uint _);
                SingletonLite<TAManager>.instance.SetScaleBuffer(MaxTreeLimit);
            }
        }
    }
}
