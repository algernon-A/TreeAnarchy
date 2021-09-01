﻿using ColossalFramework;
using ColossalFramework.Math;
using HarmonyLib;
using MoveIt;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace TreeAnarchy {
    internal partial class TAPatcher : SingletonLite<TAPatcher> {
        private void EnableTreeVariationPatches(Harmony harmony) {
            //                harmony.Patch(AccessTools.Method(typeof(TreeManager), nameof(TreeManager.CreateTree)),
            //                    postfix: new HarmonyMethod(AccessTools.Method(typeof(TreeVariation), nameof(TreeVariation.CreateTreePostfix))));
            harmony.Patch(AccessTools.Method(typeof(TreeInstance), nameof(TreeInstance.RenderInstance), new Type[] { typeof(RenderManager.CameraInfo), typeof(uint), typeof(int) }),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeInstanceRenderInstanceTranspiler))));
            harmony.Patch(AccessTools.Method(typeof(TreeInstance), nameof(TreeInstance.PopulateGroupData),
                new Type[] { typeof(uint), typeof(int), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(Vector3), typeof(RenderGroup.MeshData),
                            typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeInstancePopulateGroupDataTranspiler))));
            harmony.Patch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.RenderGeometry)),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeToolRenderGeometryTranspiler))));
            harmony.Patch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.RenderOverlay), new Type[] { typeof(RenderManager.CameraInfo) }),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeToolRenderOverlayTranspiler))));
        }

        private void DisableTreeVariationPatches(Harmony harmony) {
            harmony.Unpatch(AccessTools.Method(typeof(TreeInstance), nameof(TreeInstance.RenderInstance), new Type[] { typeof(RenderManager.CameraInfo), typeof(uint), typeof(int) }),
                HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeInstance), nameof(TreeInstance.PopulateGroupData),
                new Type[] { typeof(uint), typeof(int), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(Vector3), typeof(RenderGroup.MeshData),
                            typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }),
                HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.RenderGeometry)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.RenderOverlay), new Type[] { typeof(RenderManager.CameraInfo) }),
                HarmonyPatchType.Transpiler, HARMONYID);
        }

        private void PatchMoveItTreeVariation(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.RenderOverlay)),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(MoveableTreeRenderOverlayTranspiler))));
        }

        private void DisableMoveItTreeVariationPatches(Harmony harmony) {
            harmony.Unpatch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.RenderOverlay)), HarmonyPatchType.Transpiler, HARMONYID);
        }

        public static void CreateTreePostfix(uint tree) {
            SingletonLite<TAManager>.instance.m_treeScales[tree] = 0;
        }

        private static IEnumerable<CodeInstruction> TreeInstancePopulateGroupDataTranspiler(IEnumerable<CodeInstruction> instructions) {
            bool skip = false;
            ConstructorInfo randomizer = AccessTools.Constructor(typeof(Randomizer), new Type[] { typeof(uint) });
            foreach (var code in instructions) {
                if (!skip && code.opcode == OpCodes.Call && code.operand == randomizer) {
                    skip = true;
                    yield return code;
                    yield return new CodeInstruction(OpCodes.Ldloca_S, 3);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldloc_1);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAManager), nameof(TAManager.CalcTreeScale)));
                } else if (skip && code.opcode == OpCodes.Stloc_S && (code.operand as LocalBuilder).LocalIndex == 4) {
                    skip = false;
                    yield return code;
                } else if (!skip) {
                    yield return code;
                }
            }
        }

        private static IEnumerable<CodeInstruction> TreeInstanceRenderInstanceTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
#if ENABLETREEGROUP
            Label isGroupedTree = il.DefineLabel();
#endif
            ConstructorInfo randomizer = AccessTools.Constructor(typeof(Randomizer), new Type[] { typeof(uint) });
            FieldInfo vector4z = AccessTools.Field(typeof(Vector4), nameof(Vector4.z));
            using IEnumerator<CodeInstruction> codes = instructions.GetEnumerator();
            while (codes.MoveNext()) {
                CodeInstruction cur = codes.Current;
                if (cur.opcode == OpCodes.Call && cur.operand == randomizer) {
                    yield return cur;
                    yield return new CodeInstruction(OpCodes.Ldloca_S, 2);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAManager), nameof(TAManager.CalcTreeScale)));
                    while (codes.MoveNext()) {
                        cur = codes.Current;
                        if (cur.opcode == OpCodes.Stloc_3) {
                            yield return cur;
                            break;
                        }
                    }
                }
#if ENABLETREEGROUP
                else if (cur.StoresField(vector4z) && codes.MoveNext()) {
                    CodeInstruction next = codes.Current;
                    if (next.opcode == OpCodes.Ldarg_1 && codes.MoveNext()) {
                        CodeInstruction next1 = codes.Current;
                        if (next1.opcode == OpCodes.Ldloc_0) {
                            yield return cur;
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(TreeInstance), nameof(TreeInstance.m_flags)));
                            yield return new CodeInstruction(OpCodes.Ldc_I4_8);
                            yield return new CodeInstruction(OpCodes.And);
                            yield return new CodeInstruction(OpCodes.Brtrue_S, isGroupedTree);
                            yield return next;
                            yield return next1;
                        } else {
                            yield return cur;
                            yield return next;
                            yield return next1;
                        }
                    } else {
                        yield return cur;
                        yield return next;
                    }
                }
#endif
                else {
                    yield return cur;
                }
            }
#if ENABLETREEGROUP
            yield return new CodeInstruction(OpCodes.Ldarg_1).WithLabels(isGroupedTree);
            yield return new CodeInstruction(OpCodes.Ldarg_2);
            yield return new CodeInstruction(OpCodes.Ldloc_1);
            yield return new CodeInstruction(OpCodes.Ldloc_S, 4);
            yield return new CodeInstruction(OpCodes.Ldloc_S, 5);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAManager), nameof(TAManager.RenderGroupInstance)));
            yield return new CodeInstruction(OpCodes.Ret);
#endif
        }

        private static IEnumerable<CodeInstruction> TreeToolRenderGeometryTranspiler(IEnumerable<CodeInstruction> instructions) {
            bool skip = false;
            ConstructorInfo randomizer = AccessTools.Constructor(typeof(Randomizer), new Type[] { typeof(uint) });
            foreach (var code in instructions) {
                if (!skip && code.opcode == OpCodes.Call && code.operand == randomizer) {
                    skip = true;
                    yield return code;
                    yield return new CodeInstruction(OpCodes.Ldloca_S, 3);
                    yield return new CodeInstruction(OpCodes.Ldloc_2);
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAManager), nameof(TAManager.GetSeedTreeScale)));
                } else if (skip && code.opcode == OpCodes.Stloc_S && (code.operand as LocalBuilder).LocalIndex == 4) {
                    skip = false;
                    yield return code;
                } else if (!skip) {
                    yield return code;
                }
            }
        }

        private static IEnumerable<CodeInstruction> TreeToolRenderOverlayTranspiler(IEnumerable<CodeInstruction> instructions) {
            bool skip = false;
            ConstructorInfo randomizer = AccessTools.Constructor(typeof(Randomizer), new Type[] { typeof(uint) });
            foreach (var code in instructions) {
                if (!skip && code.opcode == OpCodes.Call && code.operand == randomizer) {
                    skip = true;
                    yield return code;
                    yield return new CodeInstruction(OpCodes.Ldloca_S, 4);
                    yield return new CodeInstruction(OpCodes.Ldloc_3);
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAManager), nameof(TAManager.GetSeedTreeScale)));
                } else if (skip && code.opcode == OpCodes.Stloc_S && (code.operand as LocalBuilder).LocalIndex == 5) {
                    skip = false;
                    yield return code;
                } else if (!skip) {
                    yield return code;
                }
            }
        }

        private static IEnumerable<CodeInstruction> MoveableTreeRenderOverlayTranspiler(IEnumerable<CodeInstruction> instructions) {
            bool skip = false;
            ConstructorInfo randomizer = AccessTools.Constructor(typeof(Randomizer), new Type[] { typeof(uint) });
            foreach (var code in instructions) {
                if (!skip && code.opcode == OpCodes.Call && code.operand == randomizer) {
                    skip = true;
                    yield return code;
                    yield return new CodeInstruction(OpCodes.Ldloca_S, 4);
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Ldloc_2);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAManager), nameof(TAManager.CalcTreeScale)));
                } else if (skip && code.opcode == OpCodes.Stloc_S && (code.operand as LocalBuilder).LocalIndex == 5) {
                    skip = false;
                    yield return code;
                } else if (!skip) {
                    yield return code;
                }
            }
        }
    }
}