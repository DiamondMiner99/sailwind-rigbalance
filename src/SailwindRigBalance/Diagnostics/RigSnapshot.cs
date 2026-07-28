using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Crest;
using HarmonyLib;
using UnityEngine;

namespace SailwindRigBalance.Diagnostics
{
    /// <summary>
    /// Step 2 of the build order: a one-shot, read-only snapshot of the sim's own force numbers for
    /// the boat you are standing on, logged so the model can be checked against reality before any
    /// UI exists. Nothing here is patched, nothing is written to the game, and nothing runs unless
    /// the hotkey is pressed.
    ///
    /// What it is proving:
    ///  - that the centre of effort really is Sail.windcenter, and that reading it needs no scale
    ///    compensation (its parent chain is logged so a Shipyard Expansion lug sail, whose windcenter
    ///    sits outside the scaled subtree, is visible as a different parent);
    ///  - that hull lateral resistance acts at the hull origin and therefore contributes almost no
    ///    yaw, so the balance is sails plus hull windage about the live centre of mass;
    ///  - that Rigidbody.centerOfMass is live rather than the authored keel value.
    /// </summary>
    public static class RigSnapshot
    {
        // Private Sail fields. These are the sim's own force components: everything else a gizmo
        // could read is either public but derived, or an inspector-only diagnostic. SailInfo 1.2.1
        // reads the same three by reflection, so this is a shipped, proven access path rather than
        // a novel one.
        private static readonly FieldInfo FUnampFwd = AccessTools.Field(typeof(Sail), "unamplifiedForwardForce");
        private static readonly FieldInfo FUnampSide = AccessTools.Field(typeof(Sail), "unamplifiedSidewayForce");
        private static readonly FieldInfo FTotalWind = AccessTools.Field(typeof(Sail), "totalWindForce");
        private static readonly FieldInfo FRelAngle = AccessTools.Field(typeof(Sail), "relativeWindAngle");

        // Crest.BoatProbes._forceHeightOffset is private; _dragInWaterRight is public.
        private static readonly FieldInfo FForceHeightOffset = AccessTools.Field(typeof(BoatProbes), "_forceHeightOffset");

        private static bool _warnedMissingFields;

        /// <summary>
        /// Reads everything and writes one block to the BepInEx log. Never throws out to Unity.
        /// </summary>
        public static void LogSnapshot()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("================ RIG BALANCE SNAPSHOT ================");

                WarnIfFieldsMissing(sb);

                Transform boat = GameState.lastBoat;
                sb.AppendLine(F("context: playing={0} inShipyard={1} lastBoat='{2}' currentBoat='{3}'",
                    GameState.playing,
                    GameState.currentShipyard != null,
                    boat == null ? "<null>" : boat.name,
                    GameState.currentBoat == null ? "<null>" : GameState.currentBoat.name));

                if (boat == null)
                {
                    sb.AppendLine("no boat: GameState.lastBoat is null. Stand on a boat and try again.");
                    Finish(sb);
                    return;
                }

                var body = boat.GetComponent<Rigidbody>();
                var refs = boat.GetComponent<BoatRefs>();
                if (body == null || refs == null)
                {
                    sb.AppendLine(F("not a boat root: Rigidbody={0} BoatRefs={1} on '{2}'",
                        body != null, refs != null, boat.name));
                    Finish(sb);
                    return;
                }

                AppendHull(sb, boat, body);
                Vector3 apparentWind = Wind.currentWind - body.velocity;
                AppendWind(sb, boat, body, apparentWind);
                AppendSails(sb, boat, body, refs);

                Finish(sb);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Snapshot] failed: " + e);
            }
        }

        private static void WarnIfFieldsMissing(StringBuilder sb)
        {
            if (_warnedMissingFields) return;
            if (FUnampFwd != null && FUnampSide != null && FTotalWind != null
                && FRelAngle != null && FForceHeightOffset != null) return;

            _warnedMissingFields = true;
            sb.AppendLine(F("WARNING: one or more private fields did not resolve "
                + "(unamplifiedForwardForce={0} unamplifiedSidewayForce={1} totalWindForce={2} "
                + "relativeWindAngle={3} _forceHeightOffset={4}). The game or Crest has changed shape; "
                + "those numbers will read 0.",
                FUnampFwd != null, FUnampSide != null, FTotalWind != null,
                FRelAngle != null, FForceHeightOffset != null));
        }

        // ---------------------------------------------------------------- hull

        private static void AppendHull(StringBuilder sb, Transform boat, Rigidbody body)
        {
            sb.AppendLine("---- hull ----");
            sb.AppendLine(F("rigidbody: mass={0} kinematic={1} speed={2} m/s",
                N(body.mass), body.isKinematic, N(body.velocity.magnitude)));
            sb.AppendLine(F("origin (rb.position)   world {0}", V(body.position)));
            sb.AppendLine(F("centerOfMass  local {0}  world {1}",
                V(body.centerOfMass), V(body.worldCenterOfMass)));

            // Is the COM live, or still the authored keel value? BoatMass.UpdateMass only runs for
            // GameState.lastBoat, and it adds a lever-arm offset for loose cargo and the aboard
            // player. If these two match exactly, nothing has shifted it and hull drag contributes
            // no yaw at all.
            var keel = boat.GetComponent<BoatKeel>();
            if (keel != null)
            {
                Vector3 delta = body.centerOfMass - keel.centerOfMass;
                sb.AppendLine(F("BoatKeel.centerOfMass  {0}   live COM differs by {1} (x/z shift is what gives hull drag any yaw at all)",
                    V(keel.centerOfMass), V(delta)));
            }
            else
            {
                sb.AppendLine("BoatKeel: absent on this hull");
            }

            var probes = boat.GetComponent<BoatProbes>();
            if (probes != null)
            {
                float heightOffset = GetFloat(FForceHeightOffset, probes);
                Vector3 dragPoint = body.position + heightOffset * Vector3.up;
                Vector3 armLocal = boat.InverseTransformVector(dragPoint - body.worldCenterOfMass);
                sb.AppendLine(F("BoatProbes: enabled={0} _dragInWaterRight={1} _forceHeightOffset={2} forcePoints={3}",
                    probes.enabled, N(probes._dragInWaterRight), N(heightOffset),
                    probes._forcePoints == null ? 0 : probes._forcePoints.Length));
                sb.AppendLine(F("  lateral drag acts at world {0}", V(dragPoint)));
                sb.AppendLine(F("  arm from COM, boat-local {0}  <- yaw needs the z (fore/aft) term; a near-zero z means no yaw from the hull",
                    V(armLocal)));
            }
            else
            {
                sb.AppendLine("BoatProbes: absent on this hull (performance mode uses BoatAlignNormal instead)");
            }

            var wind = boat.GetComponent<BoatWind>();
            if (wind != null)
            {
                Vector3 windPoint = boat.TransformPoint(wind.forceOffset);
                Vector3 armLocal = boat.InverseTransformVector(windPoint - body.worldCenterOfMass);
                sb.AppendLine(F("BoatWind: frontForce={0} sideForce={1} forceOffset={2} outAngle={3} outAppliedForce={4}",
                    N(wind.frontForce), N(wind.sideForce), V(wind.forceOffset),
                    N(wind.outAngle), N(wind.outAppliedForce)));
                sb.AppendLine(F("  windage acts at world {0}, arm from COM boat-local {1}", V(windPoint), V(armLocal)));
            }
            else
            {
                sb.AppendLine("BoatWind: absent on this hull");
            }
        }

        // ---------------------------------------------------------------- wind

        private static void AppendWind(StringBuilder sb, Transform boat, Rigidbody body, Vector3 apparentWind)
        {
            sb.AppendLine("---- wind ----");
            Vector3 local = boat.InverseTransformVector(apparentWind);
            // Which side is it on? Sign of the athwartships component of the wind's own velocity:
            // blowing toward -x means it is coming FROM starboard.
            bool fromStarboard = local.x < 0f;
            sb.AppendLine(F("true {0} ({1} m/s)   apparent {2} ({3} m/s)",
                V(Wind.currentWind), N(Wind.currentWind.magnitude), V(apparentWind), N(apparentWind.magnitude)));
            sb.AppendLine(F("apparent, boat-local {0}  -> wind is from {1}",
                V(local), fromStarboard ? "STARBOARD" : "PORT"));
        }

        // ---------------------------------------------------------------- sails

        private static void AppendSails(StringBuilder sb, Transform boat, Rigidbody body, BoatRefs refs)
        {
            sb.AppendLine("---- sails ----");

            if (refs.masts == null || refs.masts.Length == 0)
            {
                sb.AppendLine("BoatRefs.masts is empty");
                return;
            }

            Vector3 com = body.worldCenterOfMass;
            Vector3 apparentWind = Wind.currentWind - body.velocity;
            bool windFromStarboard = boat.InverseTransformVector(apparentWind).x < 0f;

            float totalYaw = 0f;
            float totalFwd = 0f;
            float totalSide = 0f;
            float totalArea = 0f;
            int counted = 0;

            // BoatRefs.masts is allocated at length 31 and indexed by Mast.orderIndex, so it is
            // sparse. Null-check every slot.
            foreach (Mast mast in refs.masts)
            {
                if (mast == null) continue;
                List<GameObject> sails = mast.sails;
                if (sails == null) continue;

                foreach (GameObject sailObject in sails)
                {
                    if (sailObject == null) continue;
                    var sail = sailObject.GetComponent<Sail>();
                    if (sail == null) continue;

                    counted++;
                    totalArea += sail.sailArea;

                    if (sail.windcenter == null)
                    {
                        sb.AppendLine(F("  [mast {0}] '{1}': windcenter is NULL, skipped",
                            mast.orderIndex, sail.sailName));
                        continue;
                    }

                    Vector3 ce = sail.windcenter.position;

                    // ApplyForce's own scaling, reproduced exactly: GetRealSailPower() folds in
                    // Debugger.debugSailAreaMult, then a per-category derate, then a flat 50, and
                    // the sideways component carries an extra 1.5 that the forward one does not.
                    float power = sail.GetRealSailPower();
                    float categoryMult = CategoryMultiplier(sail.category);
                    float unampFwd = GetFloat(FUnampFwd, sail);
                    float unampSide = GetFloat(FUnampSide, sail);
                    float fwdForce = unampFwd * power * categoryMult * 50f;
                    float sideForce = unampSide * power * categoryMult * 50f * 1.5f;

                    Vector3 force = boat.forward * fwdForce + boat.right * sideForce;
                    Vector3 arm = ce - com;
                    Vector3 torque = Vector3.Cross(arm, force);
                    float yaw = Vector3.Dot(torque, boat.up);

                    totalYaw += yaw;
                    totalFwd += fwdForce;
                    totalSide += sideForce;

                    Vector3 armLocal = boat.InverseTransformVector(arm);

                    sb.AppendLine(F("  [mast {0}] '{1}' ({2}{3}) area={4} unroll={5} upwindEff={6}",
                        mast.orderIndex, sail.sailName, sail.category,
                        sail.IsInstalled() ? "" : ", PREVIEW", N(sail.sailArea),
                        N(sail.currentUnroll), N(sail.upwindEfficiency)));
                    sb.AppendLine(F("      CE world {0}  height {1} m  heightMult {2}  parent '{3}'{4}",
                        V(ce), N(ce.y), N(HeightMult(ce.y)),
                        sail.windcenter.parent == null ? "<none>" : sail.windcenter.parent.name,
                        DescribeCeParenting(sail)));
                    sb.AppendLine(F("      appliedWindForce={0} totalWindForce={1} relWindAngle={2}",
                        N(sail.appliedWindForce), N(GetFloat(FTotalWind, sail)), N(GetFloat(FRelAngle, sail))));
                    sb.AppendLine(F("      force fwd={0} N  side={1} N   arm boat-local {2}   yaw={3} Nm",
                        N(fwdForce), N(sideForce), V(armLocal), N(yaw)));
                }
            }

            // Hull windage, the third real moment arm.
            float windageYaw = 0f;
            var boatWind = boat.GetComponent<BoatWind>();
            if (boatWind != null)
            {
                float t = Mathf.InverseLerp(0f, 90f, boatWind.outAngle);
                float coeff = Mathf.Lerp(boatWind.frontForce, boatWind.sideForce, t);
                Vector3 windageForce = apparentWind * coeff;
                Vector3 windagePoint = boat.TransformPoint(boatWind.forceOffset);
                windageYaw = Vector3.Dot(Vector3.Cross(windagePoint - com, windageForce), boat.up);
            }

            sb.AppendLine("---- totals ----");
            sb.AppendLine(F("sails counted={0}  total area={1}", counted, N(totalArea)));
            sb.AppendLine(F("sail force: fwd={0} N  side={1} N", N(totalFwd), N(totalSide)));
            sb.AppendLine(F("yaw moment about COM (boat up axis): sails={0} Nm  windage={1} Nm  sum={2} Nm",
                N(totalYaw), N(windageYaw), N(totalYaw + windageYaw)));

            // Sign convention: positive yaw turns the bow to starboard. Weather helm is the bow
            // turning INTO the wind, so the sign flips with which side the wind is on.
            float signedHelm = (totalYaw + windageYaw) * (windFromStarboard ? 1f : -1f);
            sb.AppendLine(F("SIGNED HELM = {0} Nm  ({1})   [positive = weather helm, bow turning into the wind; wind is from {2}]",
                N(signedHelm),
                signedHelm > 0f ? "WEATHER HELM" : (signedHelm < 0f ? "lee helm" : "neutral"),
                windFromStarboard ? "starboard" : "port"));
        }

        /// <summary>
        /// Vanilla's per-category derate inside ApplyForce. Junk and gaff only.
        /// </summary>
        private static float CategoryMultiplier(SailCategory category)
        {
            if (category == SailCategory.junk) return 0.75f;
            if (category == SailCategory.gaff) return 0.85f;
            return 1f;
        }

        /// <summary>
        /// Vanilla's height curve, reproduced so the snapshot can show it per sail. Vanilla feeds it
        /// RAW world Y and assumes sea level is zero, which is true at sea and false on a shipyard
        /// cradle.
        /// </summary>
        private static float HeightMult(float worldY)
        {
            return Mathf.Lerp(0.8f, 1.4f, Mathf.InverseLerp(0f, 22f, worldY));
        }

        /// <summary>
        /// The centre of effort tracks a resize only while it sits inside the transform that gets
        /// scaled. On every stock sail it does. Shipyard Expansion's own added sails have it
        /// reparented to the sail root, so they are the case worth spotting in a live log.
        /// </summary>
        private static string DescribeCeParenting(Sail sail)
        {
            try
            {
                var animator = sail.GetComponentInChildren<Animator>();
                if (animator == null) return "  [no Animator found, cannot classify]";
                bool inside = sail.windcenter.IsChildOf(animator.transform);
                return inside ? "  [CE inside the scaled part: tracks resizes]"
                              : "  [CE OUTSIDE the scaled part: frozen through resizes]";
            }
            catch
            {
                return "";
            }
        }

        // ---------------------------------------------------------------- helpers

        private static float GetFloat(FieldInfo field, object target)
        {
            if (field == null || target == null) return 0f;
            try
            {
                object value = field.GetValue(target);
                return value is float f ? f : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static string N(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture,
            "({0:F3}, {1:F3}, {2:F3})", v.x, v.y, v.z);

        private static string F(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);

        private static void Finish(StringBuilder sb)
        {
            sb.AppendLine("======================================================");
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
