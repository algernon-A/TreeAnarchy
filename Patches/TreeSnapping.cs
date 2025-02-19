﻿using ColossalFramework;
using ColossalFramework.Math;
using HarmonyLib;
using MoveIt;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;
using static TreeAnarchy.TAMod;

namespace TreeAnarchy {
    internal static partial class TAPatcher {
        private const float errorMargin = 0.075f;
        private const ushort FixedHeightMask = unchecked((ushort)~TreeInstance.Flags.FixedHeight);
        private const ushort FixedHeightFlag = unchecked((ushort)TreeInstance.Flags.FixedHeight);
        private static void EnableTreeSnappingPatches(Harmony harmony) {
            try {
                harmony.Patch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.SimulationStep)),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeToolSimulationStepTranspiler))));
            } catch (Exception e) {
                TALog("Failed to patch TreeTool::SimulationStep");
                TALog(e.Message);
                throw;
            }
            try {
                harmony.Patch(AccessTools.Method(typeof(TreeTool).GetNestedType("<CreateTree>c__Iterator0", BindingFlags.Instance | BindingFlags.NonPublic), "MoveNext"),
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(TreeToolCreateTreeTranspiler))));
            } catch (Exception e) {
                TALog("Failed to patch TreeTool::CreateTree");
                TALog(e.Message);
                throw;
            }
        }

        private static void PatchMoveItSnapping(Harmony harmony) {
            try {
                harmony.Patch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.Transform)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(MoveableTreeTransformPrefix))));
            } catch (Exception e) {
                TALog("Failed to patch MoveIt::MoveableTree::Transform, this is non-Fatal");
                TALog(e.Message);
            }
            try {
                harmony.Patch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.RenderCloneGeometry)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(RenderCloneGeometryPrefix))));
            } catch (Exception e) {
                TALog("Failed to patch MoveIt::MoveableTree::RenderCloneGeometry, this is non-Fatal");
                TALog(e.Message);
            }
            try {
                harmony.Patch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.RenderCloneOverlay)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(RenderCloneOverlayPrefix))));
            } catch (Exception e) {
                TALog("Failed to patch MoveIt::MoveableTree::RenderCloneOverlay, this is non-Fatal");
                TALog(e.Message);
            }
            try {
                harmony.Patch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.Clone),
                    new Type[] { typeof(InstanceState), typeof(Matrix4x4).MakeByRefType(),
                typeof(float), typeof(float), typeof(Vector3), typeof(bool), typeof(Dictionary<ushort, ushort>), typeof(MoveIt.Action) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(TAPatcher), nameof(ClonePrefix))));
            } catch (Exception e) {
                TALog("Failed to patch MoveIt::MoveableTree::Clone, this is non-Fatal");
                TALog(e.Message);
            }
        }

        private static void DisableTreeSnappingPatches(Harmony harmony) {
            harmony.Unpatch(AccessTools.Method(typeof(TreeTool), nameof(TreeTool.SimulationStep)), HarmonyPatchType.Transpiler, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(TreeTool).GetNestedType("<CreateTree>c__Iterator0", BindingFlags.Instance | BindingFlags.NonPublic), "MoveNext"), HarmonyPatchType.Transpiler, HARMONYID);
        }

        private static void DisableMoveItSnappingPatches(Harmony harmony) {
            harmony.Unpatch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.Transform)), HarmonyPatchType.Prefix, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.RenderCloneGeometry)), HarmonyPatchType.Prefix, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.RenderCloneOverlay)), HarmonyPatchType.Prefix, HARMONYID);
            harmony.Unpatch(AccessTools.Method(typeof(MoveableTree), nameof(MoveableTree.Clone),
                new Type[] { typeof(InstanceState), typeof(Matrix4x4).MakeByRefType(),
                typeof(float), typeof(float), typeof(Vector3), typeof(bool), typeof(Dictionary<ushort, ushort>), typeof(MoveIt.Action) }),
                HarmonyPatchType.Prefix, HARMONYID);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ConfigureRaycastInput(ref ToolBase.RaycastInput input) {
            if (UseTreeSnapping) {
                input.m_currentEditObject = false;
                input.m_ignoreTerrain = false;
                input.m_ignoreBuildingFlags = Building.Flags.None;
                input.m_ignoreNodeFlags = NetNode.Flags.None;
                input.m_ignoreSegmentFlags = NetSegment.Flags.None;
                input.m_ignorePropFlags = PropInstance.Flags.None;
                input.m_propService = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
                input.m_buildingService = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
                input.m_netService = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
                input.m_netService2 = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CalcFixedHeight(uint treeID) {
            TreeInstance[] trees = Singleton<TreeManager>.instance.m_trees.m_buffer;
            Vector3 position = trees[treeID].Position;
            float terrainHeight = Singleton<TerrainManager>.instance.SampleDetailHeight(position);
            if (position.y > terrainHeight + errorMargin) {
                trees[treeID].m_flags |= FixedHeightFlag;
            } else {
                trees[treeID].m_flags &= FixedHeightMask;
            }
        }

        private static IEnumerable<CodeInstruction> TreeToolCreateTreeTranspiler(IEnumerable<CodeInstruction> instructions) {
            MethodInfo placementEffect = AccessTools.Method(typeof(TreeTool), nameof(TreeTool.DispatchPlacementEffect));
            using (IEnumerator<CodeInstruction> codes = instructions.GetEnumerator()) {
                while (codes.MoveNext()) {
                    CodeInstruction cur = codes.Current;
                    if (cur.opcode == OpCodes.Call && cur.operand == placementEffect) {
                        yield return cur;
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 4);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(CalcFixedHeight)));
                    } else {
                        yield return cur;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool GetFixedHeight(Vector3 position) {
            float terrainHeight = Singleton<TerrainManager>.instance.SampleDetailHeight(position);
            float positionY = position.y;
            if (positionY > terrainHeight + errorMargin || positionY < terrainHeight - errorMargin) {
                return true;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> TreeToolSimulationStepTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            int cachedRayInputIndex = -1;
            int outputOccuranceCount = 0;
            int sigFoundCount = 0;
            Label TreeSnapEnabled = il.DefineLabel();
            Label TreeSnapDisabled = il.DefineLabel();
            ConstructorInfo inputConstructor = AccessTools.Constructor(typeof(ToolBase.RaycastInput), new Type[] { typeof(Ray), typeof(float) });
            FieldInfo rayOutputObject = AccessTools.Field(typeof(ToolBase.RaycastOutput), nameof(ToolBase.RaycastOutput.m_currentEditObject));
            Type EMLRaycastOutputType = Type.GetType("EManagersLib.EToolBase+RaycastOutput");
            FieldInfo rayOutputObjectEML = AccessTools.Field(EMLRaycastOutputType, "m_currentEditObject");
            using (var codes = instructions.GetEnumerator()) {
                while (codes.MoveNext()) {
                    var cur = codes.Current;
                    if (cachedRayInputIndex < 0 && cur.opcode == OpCodes.Ldloca_S && cur.operand is LocalBuilder l1) {
                        cachedRayInputIndex = l1.LocalIndex;
                        yield return cur;
                    } else if (cachedRayInputIndex >= 0 && cur.opcode == OpCodes.Call && cur.operand == inputConstructor) {
                        yield return cur;
                        yield return new CodeInstruction(OpCodes.Ldloca_S, cachedRayInputIndex);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(TAPatcher.ConfigureRaycastInput)));
                    } else if (outputOccuranceCount == 0 && cur.opcode == OpCodes.Ldfld && (cur.operand == rayOutputObject || cur.operand == rayOutputObjectEML)) {
                        outputOccuranceCount++;
                        yield return cur;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(TAMod.UseTreeSnapping)));
                        yield return new CodeInstruction(OpCodes.Or);
                    } else if (outputOccuranceCount == 1 && cur.opcode == OpCodes.Ldfld && (cur.operand == rayOutputObject || cur.operand == rayOutputObjectEML)) {
                        outputOccuranceCount++;
                        yield return cur;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(TAMod.UseTreeSnapping)));
                        yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                        yield return new CodeInstruction(OpCodes.Ceq);
                        yield return new CodeInstruction(OpCodes.Or);
                    } else if (cur.opcode == OpCodes.Ldarg_0 && codes.MoveNext()) {
                        var next = codes.Current;
                        if (next.opcode == OpCodes.Ldloca_S && next.operand is LocalBuilder l3 && (l3.LocalType == typeof(ToolBase.RaycastOutput) || l3.LocalType == EMLRaycastOutputType)) {
                            if (++sigFoundCount == 3) {
                                yield return cur;
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TAMod), nameof(TAMod.UseTreeSnapping)));
                                yield return new CodeInstruction(OpCodes.Brtrue_S, TreeSnapEnabled);
                                yield return next;
                                codes.MoveNext();
                                yield return codes.Current;
                                yield return new CodeInstruction(OpCodes.Br_S, TreeSnapDisabled);
                                yield return new CodeInstruction(OpCodes.Ldloca_S, l3.LocalIndex).WithLabels(TreeSnapEnabled);
                                if (l3.LocalType == typeof(ToolBase.RaycastOutput)) {
                                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ToolBase.RaycastOutput), nameof(ToolBase.RaycastOutput.m_hitPos)));
                                } else {
                                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(EMLRaycastOutputType, "m_hitPos"));
                                }
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TAPatcher), nameof(TAPatcher.GetFixedHeight)));
                                codes.MoveNext();
                                yield return codes.Current.WithLabels(TreeSnapDisabled);
                            } else {
                                yield return cur;
                                yield return next;
                            }
                        } else {
                            yield return cur;
                            yield return next;
                        }
                    } else {
                        yield return cur;
                    }
                }
            }
        }

        /* If tree snapping is false => 
         * check if tree has fixedheight flag, if not then follow the terrain
         * 
         * If tree snapping is on =>
         * If following terrain is turned on, then just follow terrain no matter what.
         * If tree snapping is true, then check for deltaheight to see if user raised the tree or not
         * if deltaheight is 0, then raycast to see if it hit any object to snap to
         * If after raycast, no object is hit, then use default follow terrainheight
         */
        private static Vector3 SampleTreeSnapVector(MoveableTree instance, InstanceState state, ref Matrix4x4 matrix4x, float deltaHeight, Vector3 center, bool followTerrain) {
            Vector3 newPosition = matrix4x.MultiplyPoint(state.position - center);
            newPosition.y = state.position.y + deltaHeight;

            float newTerrainHeight = Singleton<TerrainManager>.instance.SampleDetailHeight(newPosition);
            TreeInstance[] trees = Singleton<TreeManager>.instance.m_trees.m_buffer;
            uint treeID = instance.id.Tree;

            if (!UseTreeSnapping) {
                if ((trees[treeID].m_flags & FixedHeightFlag) == 0) {
                    newPosition.y = newTerrainHeight;
                }
            } else if (followTerrain) {
                trees[treeID].m_flags &= FixedHeightMask;
                newPosition.y = newTerrainHeight;
            } else {
                if (deltaHeight != 0) {
                    trees[treeID].m_flags |= FixedHeightFlag;
                } else {
                    if (!TreeSnapRayCast(newPosition, out newPosition)) {
                        newPosition.y = newTerrainHeight;
                    } else {
                        if (newPosition.y > newTerrainHeight + errorMargin || newPosition.y < newTerrainHeight - errorMargin) {
                            trees[treeID].m_flags |= FixedHeightFlag;
                        }
                        /* seems after snapping to a building with y position > 0, then tree position gets all messed up
                            * so we have to reset the position in the cases where position is back to terrain height +- 0.075f */
                        if (newPosition.y >= (newTerrainHeight - errorMargin) && newPosition.y <= (newTerrainHeight + errorMargin)) {
                            newPosition.y = newTerrainHeight;
                            trees[treeID].m_flags &= FixedHeightMask;
                            state.position.y = newTerrainHeight;
                        }
                    }
                }
            }

            return newPosition;
        }

        /* Must call method instead of adding in this routine. Harmony issue workaround */
        private static bool MoveableTreeTransformPrefix(MoveableTree __instance, InstanceState state, ref Matrix4x4 matrix4x, float deltaHeight, Vector3 center, bool followTerrain) {
            Vector3 vector = SampleTreeSnapVector(__instance, state, ref matrix4x, deltaHeight, center, followTerrain);
            __instance.Move(vector, 0f);
            return false;
        }

        private static bool RenderCloneGeometryPrefix(InstanceState instanceState, ref Matrix4x4 matrix4x, Vector3 deltaPosition, Vector3 center, bool followTerrain, RenderManager.CameraInfo cameraInfo) {
            TreeState treeState = instanceState as TreeState;
            TreeInfo treeInfo = treeState.Info.Prefab as TreeInfo;
            float scale = TAManager.m_extraTreeInfos[treeState.instance.id.Tree].TreeScale;
            float brightness = TAManager.m_extraTreeInfos[treeState.instance.id.Tree].m_brightness;
            Vector3 vector = matrix4x.MultiplyPoint(treeState.position - center);
            vector.y = treeState.position.y + deltaPosition.y;
            vector = CalculateTreeVector(vector, deltaPosition.y, followTerrain);

            TreeInstance.RenderInstance(cameraInfo, treeInfo, vector, scale, brightness, RenderManager.DefaultColorLocation, false);
            return false;
        }

        private static bool RenderCloneOverlayPrefix(InstanceState instanceState, ref Matrix4x4 matrix4x, Vector3 deltaPosition, Vector3 center, bool followTerrain, RenderManager.CameraInfo cameraInfo, Color toolColor) {
            TreeState treeState = instanceState as TreeState;
            TreeInfo treeInfo = treeState.Info.Prefab as TreeInfo;
            float scale = TAManager.m_extraTreeInfos[treeState.instance.id.Tree].TreeScale;
            Vector3 vector = matrix4x.MultiplyPoint(treeState.position - center);
            vector.y = treeState.position.y + deltaPosition.y;
            vector = CalculateTreeVector(vector, deltaPosition.y, followTerrain);

            TreeTool.RenderOverlay(cameraInfo, treeInfo, vector, scale, toolColor);
            return false;
        }

        /* three situations where raycast position is used 
         * When deltaHeight is == 0
         * When position.y is at terrainHeight +- errorMargin
         * When raycastPosition.y > terrainHeight
         */
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 CalculateTreeVector(Vector3 position, float deltaHeight, bool followTerrain) {
            float terrainHeight = Singleton<TerrainManager>.instance.SampleDetailHeight(position);
            if (!UseTreeSnapping || followTerrain) {
                position.y = terrainHeight;
            } else if (TreeSnapRayCast(position, out Vector3 raycastPosition)) {
                position = raycastPosition;
            } else if (deltaHeight == 0) {
                position.y = terrainHeight;
            }
            return position;
        }

        private static bool ClonePrefix(ref Instance __result, InstanceState instanceState, ref Matrix4x4 matrix4x, float deltaHeight, Vector3 center, bool followTerrain) {
            TreeState treeState = instanceState as TreeState;
            Vector3 vector = matrix4x.MultiplyPoint(treeState.position - center);
            vector.y = treeState.position.y + deltaHeight;
            vector = CalculateTreeVector(vector, deltaHeight, followTerrain);
            __result = null;
            TreeInstance[] buffer = Singleton<TreeManager>.instance.m_trees.m_buffer;
            if (Singleton<TreeManager>.instance.CreateTree(out uint tree, ref Singleton<SimulationManager>.instance.m_randomizer, treeState.Info.Prefab as TreeInfo, vector, treeState.single)) {
                __result = new MoveableTree(new InstanceID {
                    Tree = tree
                }) {
                    position = vector
                };
                if (!followTerrain && (deltaHeight != 0 || treeState.position.y > treeState.terrainHeight + errorMargin || treeState.position.y < treeState.terrainHeight - errorMargin)) {
                    buffer[tree].m_flags |= FixedHeightFlag;
                }
            }

            return false;
        }

        private static bool RayCast(ToolBase.RaycastInput input, out ToolBase.RaycastOutput output) {
            float tempRayLength;
            Vector3 origin = input.m_ray.origin;
            Vector3 normalized = input.m_ray.direction.normalized;
            Vector3 vector = input.m_ray.origin + normalized * input.m_length;
            Segment3 ray = new Segment3(origin, vector);
            output.m_hitPos = vector;
            output.m_overlayButtonIndex = 0;
            output.m_netNode = 0;
            output.m_netSegment = 0;
            output.m_building = 0;
            output.m_propInstance = 0;
            output.m_treeInstance = 0u;
            output.m_vehicle = 0;
            output.m_parkedVehicle = 0;
            output.m_citizenInstance = 0;
            output.m_transportLine = 0;
            output.m_transportStopIndex = 0;
            output.m_transportSegmentIndex = 0;
            output.m_district = 0;
            output.m_park = 0;
            output.m_disaster = 0;
            output.m_currentEditObject = false;
            bool result = false;
            float mouseRayLength = input.m_length;
            if (!input.m_ignoreTerrain && Singleton<TerrainManager>.instance.RayCast(ray, out Vector3 vector2)) {
                float rayLength = Vector3.Distance(vector2, origin) + 100f;
                if (rayLength < mouseRayLength) {
                    output.m_hitPos = vector2;
                    result = true;
                    mouseRayLength = rayLength;
                }
            }
            if ((input.m_ignoreNodeFlags != NetNode.Flags.All ||
                 input.m_ignoreSegmentFlags != NetSegment.Flags.All) && Singleton<NetManager>.instance.RayCast(input.m_buildObject as NetInfo, ray, input.m_netSnap, input.m_segmentNameOnly, input.m_netService.m_service, input.m_netService2.m_service, input.m_netService.m_subService, input.m_netService2.m_subService, input.m_netService.m_itemLayers, input.m_netService2.m_itemLayers, input.m_ignoreNodeFlags, input.m_ignoreSegmentFlags, out vector2, out output.m_netNode, out output.m_netSegment)) {
                tempRayLength = Vector3.Distance(vector2, origin);
                if (tempRayLength < mouseRayLength) {
                    output.m_hitPos = vector2;
                    result = true;
                    mouseRayLength = tempRayLength;
                } else {
                    output.m_netNode = 0;
                    output.m_netSegment = 0;
                }
            }
            if (input.m_ignoreBuildingFlags != Building.Flags.All && Singleton<BuildingManager>.instance.RayCast(ray, input.m_buildingService.m_service, input.m_buildingService.m_subService, input.m_buildingService.m_itemLayers, input.m_ignoreBuildingFlags, out vector2, out output.m_building)) {
                tempRayLength = Vector3.Distance(vector2, origin);
                if (tempRayLength < mouseRayLength) {
                    output.m_hitPos = vector2;
                    output.m_netNode = 0;
                    output.m_netSegment = 0;
                    result = true;
                    mouseRayLength = tempRayLength;
                } else {
                    output.m_building = 0;
                }
            }
            if (input.m_currentEditObject && Singleton<ToolManager>.instance.m_properties.RaycastEditObject(ray, out vector2)) {
                tempRayLength = Vector3.Distance(vector2, origin);
                if (tempRayLength < mouseRayLength) {
                    output.m_hitPos = vector2;
                    output.m_netNode = 0;
                    output.m_netSegment = 0;
                    output.m_building = 0;
                    output.m_disaster = 0;
                    output.m_currentEditObject = true;
                    result = true;
                    mouseRayLength = tempRayLength;
                }
            }
            if (input.m_ignorePropFlags != PropInstance.Flags.All && Singleton<PropManager>.instance.RayCast(ray, input.m_propService.m_service, input.m_propService.m_subService, input.m_propService.m_itemLayers, input.m_ignorePropFlags, out vector2, out output.m_propInstance)) {
                if (Vector3.Distance(vector2, origin) - 0.5f < mouseRayLength) {
                    output.m_hitPos = vector2;
                    output.m_netNode = 0;
                    output.m_netSegment = 0;
                    output.m_building = 0;
                    output.m_disaster = 0;
                    output.m_currentEditObject = false;
                    result = true;
                } else {
                    output.m_propInstance = 0;
                }
            }
            return result;
        }

        // These codes should really live in the MoveIt mod space and not here!!
        /* As you can see here, I tried to implement raycast for tree snapping to work with MoveIt mod,
         * but apparently MoveIt calculates position differently than CO Framework. If this was ever to 
         * be realized, I think it has to be done from within MoveIt mod.
         */
        private static bool TreeSnapRayCast(Vector3 position, out Vector3 vector) {
            Ray objRay;
            if (!UseExperimentalTreeSnapping) {
                vector = position;
                return false;
            }

            objRay = Camera.main.ScreenPointToRay(Camera.main.WorldToScreenPoint(position));
            ToolBase.RaycastInput input = new ToolBase.RaycastInput(objRay, Camera.main.farClipPlane) {
                m_ignoreTerrain = true
            };
            if (UseTreeSnapToBuilding) {
                input.m_ignoreBuildingFlags = Building.Flags.None;
                input.m_buildingService = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
            }
            if (UseTreeSnapToNetwork) {
                input.m_ignoreNodeFlags = NetNode.Flags.None;
                input.m_ignoreSegmentFlags = NetSegment.Flags.None;
                input.m_netService = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
                input.m_netService2 = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
            }
            if (UseTreeSnapToProp) {
                input.m_ignorePropFlags = PropInstance.Flags.None;
                input.m_propService = new ToolBase.RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.Default);
            }
            if (RayCast(input, out ToolBase.RaycastOutput raycastOutput)) {
                vector = raycastOutput.m_hitPos;
                return true;
            }
            vector = position;
            return false;
        }
    }
}
