﻿#if ENABLETREEANARCHY
using ColossalFramework;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TreeAnarchy {
    internal partial class TAPatcher : SingletonLite<TAPatcher> {
        private Type m_redirectUtil = null;
        private static MethodInfo m_redirectMethod = null;

        private void PatchPTA(Harmony harmony) {
            if (m_redirectUtil is null && (IsPluginExists(593588108, "Prop & Tree Anarchy") || IsPluginExists(2456344023, "Prop & Tree Anarchy"))) {
                m_redirectUtil = Assembly.Load("PropAnarchy").GetType("PropAnarchy.Redirection.RedirectionUtil");
                foreach (var methodInfo in m_redirectUtil.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)) {
                    if (methodInfo.Name == "RedirectMethod" && methodInfo.GetParameters()[2].ToString().Contains("Dictionary")) {
                        m_redirectMethod = methodInfo;
                    }
                }
                harmony.Patch(AccessTools.Method(m_redirectUtil, "RedirectMethods"),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(PTARedirectMethodsTranspiler))));
            }
        }

        private void UnpatchPTA(Harmony harmony) {
            if (!(m_redirectUtil is null)) {
                harmony.Unpatch(AccessTools.Method(m_redirectUtil, "RedirectMethods"), HarmonyPatchType.Transpiler, HARMONYID);
                m_redirectUtil = null;
            }
        }

        private void EnableTreeAnarchyPatches(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.CheckPlacementErrors)),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeToolCheckPlacementErrorsTranspiler))));
            harmony.Patch(AccessTools.Method(typeof(TreeInstance), "CheckOverlap"),
                transpiler: new HarmonyMethod(typeof(TAPatcher), nameof(TreeInstanceCheckOverlapTranspiler)));
            harmony.Patch(AccessTools.PropertySetter(typeof(TreeInstance), nameof(TreeInstance.GrowState)),
                transpiler: new HarmonyMethod(typeof(TAPatcher), nameof(TreeInstanceSetGrowStateTranspiler)));
        }

        private void DisableTreeAnarchyPatches(Harmony harmony) {
            harmony.Unpatch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.CheckPlacementErrors)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeInstance), "CheckOverlap"), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.PropertySetter(typeof(TreeInstance), nameof(TreeInstance.GrowState)), HarmonyPatchType.Transpiler, HARMONYID);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void QueuedAction(object treeID) => Singleton<TreeManager>.instance.ReleaseTree((uint)treeID);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReleaseTreeQueue(uint treeID) => ThreadPool.QueueUserWorkItem(QueuedAction, treeID);

        public static void CustomRedirect(Type targetType, MethodInfo method, object redirects, bool reverse = false) {
            switch (method.Name) {
            case @"set_GrowState":
            case @"CheckOverlap":
            case @"CheckPlacementErrors":
                Type type = method.GetParameters().First().ParameterType;
                if (type == typeof(PropInstance).MakeByRefType() || type == typeof(PropInfo)) {
                    goto runDefault;
                }
                TAMod.TALog($"Overriding Prop & Tree Anarchy Redirect: {method}");
                return;
            default:
runDefault:
                m_redirectMethod.Invoke(null, new object[] {
                    targetType,
                    method,
                    redirects,
                    reverse
                });
                break;
            }
        }

        /* A patch to the patcher of Prop Tree Anarchy */
        private static IEnumerable<CodeInstruction> PTARedirectMethodsTranspiler(IEnumerable<CodeInstruction> instructions) {
            Type redirectUtil = Assembly.Load("PropAnarchy").GetType("PropAnarchy.Redirection.RedirectionUtil");
            foreach (var code in instructions) {
                if (code.opcode == OpCodes.Call && code.ToString().Contains("RedirectMethod")) {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(TAPatcher.CustomRedirect)));
                } else yield return code;
            }
        }

        private static IEnumerable<CodeInstruction> TreeToolCheckPlacementErrorsTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            Label TreeAnarchyDisabled = il.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(TAMod), nameof(TAMod.UseTreeAnarchy)));
            yield return new CodeInstruction(OpCodes.Brfalse_S, TreeAnarchyDisabled);
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Conv_I8);
            yield return new CodeInstruction(OpCodes.Ret);
            using (IEnumerator<CodeInstruction> codes = instructions.GetEnumerator()) {
                if (codes.MoveNext()) codes.Current.WithLabels(TreeAnarchyDisabled);
                do {
                    yield return codes.Current;
                } while (codes.MoveNext());
            }
        }

        public static bool CheckAnarchyState(ref TreeInstance tree) {
            if (Singleton<LoadingManager>.instance.m_currentlyLoading) {
                return true;
            } else if (TAMod.UseTreeAnarchy) {
                ToolBase currentTool = ToolsModifierControl.GetCurrentTool<ToolBase>();
                if (!(currentTool is NetTool) && !(currentTool is BuildingTool) && !(currentTool is BulldozeTool)) {
                    if (tree.GrowState == 0) {
                        tree.GrowState = 1;
                        DistrictManager district = Singleton<DistrictManager>.instance;
                        byte park = district.GetPark(tree.Position);
                        district.m_parks.m_buffer[park].m_treeCount++;
                    }
                }
                return true;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> TreeInstanceCheckOverlapTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            bool sigFound = false;
            CodeInstruction prevPrev = default, prev = default;
            Label Exit = il.DefineLabel();
            MethodInfo get_growState = AccessTools.PropertyGetter(typeof(TreeInstance), nameof(TreeInstance.GrowState));
            foreach (var code in instructions) {
                if (!sigFound && prevPrev?.opcode == OpCodes.Ldloc_0 && prev?.opcode == OpCodes.Brtrue && code.opcode == OpCodes.Ret) {
                    sigFound = true;
                    yield return prevPrev;
                    yield return new CodeInstruction(OpCodes.Brfalse_S, Exit);
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(CheckAnarchyState)));
                    yield return new CodeInstruction(OpCodes.Brtrue_S, Exit);
                    prev = (prevPrev = null);
                } else if (sigFound && prevPrev?.opcode == OpCodes.Br && prev?.opcode == OpCodes.Ldarg_0 && code.opcode == OpCodes.Call && code.operand == get_growState) {
                    yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(TAMod), nameof(TAMod.DeleteOnOverlap)));
                    yield return new CodeInstruction(OpCodes.Brfalse_S, Exit);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(ReleaseTreeQueue)));
                    yield return prevPrev;
                    yield return prev;
                    prev = (prevPrev = null);
                } else if (!(prevPrev is null) && !(prev is null)) {
                    yield return prevPrev;
                    yield return prev;
                    prev = (prevPrev = null);
                }
                prevPrev = prev;
                prev = code;
            }
            if (!(prevPrev is null)) yield return prevPrev;
            if (!(prev is null)) yield return prev;
        }

        public static bool GetAnarchyState(int val) {
            if (TAMod.UseTreeAnarchy && val == 0) return true;
            return false;
        }

        private static IEnumerable<CodeInstruction> TreeInstanceSetGrowStateTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            Label valueNotZero = il.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(GetAnarchyState)));
            yield return new CodeInstruction(OpCodes.Brfalse_S, valueNotZero);
            yield return new CodeInstruction(OpCodes.Ret);
            using (IEnumerator<CodeInstruction> codes = instructions.GetEnumerator()) {
                if (codes.MoveNext()) codes.Current.WithLabels(valueNotZero);
                do {
                    yield return codes.Current;
                } while (codes.MoveNext());
            }
        }
    }
}
#endif