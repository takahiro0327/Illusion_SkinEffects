﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ActionGame;
using HarmonyLib;
using KKAPI.MainGame;
using Manager;
using UnityEngine;

namespace KK_SkinEffects
{
    /// <summary>
    /// Used for keeping state of chracters in the main game
    /// </summary>
    internal class SkinEffectGameController : GameCustomFunctionController
    {
        private static readonly Dictionary<SaveData.Heroine, IDictionary<string, object>> _persistentCharaState = new Dictionary<SaveData.Heroine, IDictionary<string, object>>();
        private static readonly HashSet<SaveData.Heroine> _disableDeflowering = new HashSet<SaveData.Heroine>();

        protected override void OnPeriodChange(Cycle.Type period)
        {
            ClearCharaState();
        }

        protected override void OnDayChange(Cycle.Week day)
        {
            ClearCharaState();
            _disableDeflowering.Clear();
        }

        protected override void OnStartH(HSceneProc proc, bool freeH)
        {
            StopAllCoroutines();

            // Prevent the HymenRegen taking effect every time H is done in a day
            foreach (var heroine in proc.flags.lstHeroine)
                heroine.chaCtrl.GetComponent<SkinEffectsController>().DisableDeflowering = _disableDeflowering.Contains(heroine);

            proc.StartCoroutine(HsceneUpdate(proc, freeH));
        }

        protected override void OnEndH(HSceneProc proc, bool freeH)
        {
            if (freeH || !SkinEffectsPlugin.EnablePersistence.Value) return;

            var isShower = proc.flags.IsShowerPeeping();
            foreach (var heroine in proc.flags.lstHeroine)
            {
                if (isShower)
                {
                    // Clear effects after a shower, save them after other types of h scenes
                    _persistentCharaState.Remove(heroine);
                }
                else
                {
                    var controller = heroine.chaCtrl.GetComponent<SkinEffectsController>();
                    SavePersistData(heroine, controller);

                    if (controller.DisableDeflowering)
                        _disableDeflowering.Add(heroine);
                }

                StartCoroutine(RefreshOnSceneChangeCo(heroine, true));
            }
        }

        /// <summary>
        /// Runs during h scene
        /// Handles butt blushing
        /// </summary>
        private static IEnumerator HsceneUpdate(HSceneProc proc, bool freeH)
        {
            yield return new WaitWhile(() => Scene.Instance.IsNowLoadingFade);

            var controllers = proc.flags.lstHeroine.Select(x => x?.chaCtrl != null ? x.chaCtrl.GetComponent<SkinEffectsController>() : null).ToArray();
            var roughTouchTimers = new float[controllers.Length];

            var hands = new[] { proc.hand, proc.hand1 };
            var aibuItems = Traverse.Create(proc.hand).Field<HandCtrl.AibuItem[]>("useItems").Value;

            while (proc)
            {
                var isAibu = proc.flags.mode == HFlag.EMode.aibu;
                var fastSpeed = proc.flags.speed >= 1f; // max 1.5
                var slowSpeed = proc.flags.speed >= 0.5f;
                for (int i = 0; i < controllers.Length; i++)
                {
                    var ctrl = controllers[i];
                    if (ctrl == null) continue;

                    var anyChanged = false;
                    var hand = hands[i];
                    var timer = roughTouchTimers[i];

                    // Touching during 3p and some other positions, also additional contact damage during touch position
                    var kindTouch = hand.SelectKindTouch;
                    var touchedSiri = kindTouch == HandCtrl.AibuColliderKind.siriL ||
                                      kindTouch == HandCtrl.AibuColliderKind.siriR ||
                                      kindTouch == HandCtrl.AibuColliderKind.reac_bodydown;
                    if (touchedSiri && hand.hitReaction.IsPlay())
                    {
                        timer += Time.deltaTime * 2;
                        anyChanged = true;
                    }

                    // Touching during touch position, only works in 1v1 scenes not 3p
                    if (i == 0 && isAibu && (fastSpeed || slowSpeed))
                    {
                        if (aibuItems.Any(x => x != null && (x.kindTouch == HandCtrl.AibuColliderKind.siriL || x.kindTouch == HandCtrl.AibuColliderKind.siriR)))
                        {
                            timer += fastSpeed ? Time.deltaTime : Time.deltaTime / 2;
                            anyChanged = true;
                        }
                    }

                    // Slow decay
                    if (!anyChanged) timer = Mathf.Max(0, timer - Time.deltaTime / 9);

                    roughTouchTimers[i] = timer;
                    var level = (int)(timer / 10);
                    // Don't go back to 0, the change is too noticeable also doesn't really make sense
                    if (level != 0) ctrl.ButtLevel = level;

                    //if (timer > 0) Console.WriteLine(timer);
                }

                yield return null;
            }
        }

        /// <summary>
        /// Needed to apply new state to the copy of current character used outside of current scene.
        /// Must be called before the current scene exits. Can happen for Talk > Roaming, Talk > H, H > Roaming
        /// </summary>
        private static IEnumerator RefreshOnSceneChangeCo(SaveData.Heroine heroine, bool afterH)
        {
            // Store reference to the character copy used in current scene
            var previousControl = heroine.chaCtrl;
            // Wait until we switch from temporary character copy to the character used in the next scene
            yield return new WaitUntil(() => heroine.chaCtrl != previousControl && heroine.chaCtrl != null);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            // Apply the stored state from h scene
            var controller = heroine.chaCtrl.GetComponent<SkinEffectsController>();
            ApplyPersistData(controller);

            if (afterH)
            {
                if (Game.Instance != null && Game.Instance.actScene != null)
                {
                    // Make the girl want to take a shower after H. Index 2 is shower
                    var actCtrl = Game.Instance.actScene.actCtrl;
                    actCtrl?.SetDesire(2, heroine, 200);
                }

                // Slowly remove sweat effects as she "cools down"
                while (controller.SweatLevel > 0 || controller.TearLevel > 0)
                {
                    yield return new WaitForSeconds(60);

                    if (Scene.Instance.IsNowLoadingFade) break;

                    if (controller.SweatLevel > 0) controller.SweatLevel--;
                    if (controller.TearLevel > 0) controller.TearLevel--;
                    if (controller.DroolLevel > 0) controller.DroolLevel--;
                }
            }
        }

        private static void ClearCharaState()
        {
            foreach (var heroine in _persistentCharaState.Keys)
            {
                var chaCtrl = heroine.chaCtrl;
                if (chaCtrl != null)
                    chaCtrl.GetComponent<SkinEffectsController>().ClearCharaState(true);
            }

            _persistentCharaState.Clear();
        }

        public static void ApplyPersistData(SkinEffectsController controller)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));

            IDictionary<string, object> stateDict = null;

            var heroine = controller.ChaControl.GetHeroine();
            if (heroine != null)
                _persistentCharaState.TryGetValue(heroine, out stateDict);

            controller.ApplyCharaState(stateDict);
        }

        internal void OnSceneUnload(SaveData.Heroine heroine, SkinEffectsController controller)
        {
            StartCoroutine(RefreshOnSceneChangeCo(heroine, false));
        }

        public static void SavePersistData(SaveData.Heroine heroine, SkinEffectsController controller)
        {
            if (heroine == null) throw new ArgumentNullException(nameof(heroine));
            if (controller == null) throw new ArgumentNullException(nameof(controller));

            _persistentCharaState.TryGetValue(heroine, out var dict);
            if (dict == null)
                _persistentCharaState[heroine] = dict = new Dictionary<string, object>();

            controller.WriteCharaState(dict);
        }
    }
}
