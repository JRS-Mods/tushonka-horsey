using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// How the horse moves: a body-shaped capsule (tilted with the slope it stands on) and a head, swept along the
    /// ground against the same colliders your character walks into; a ray for the ground under it (steps up to half a
    /// metre; more is a wall or a jump); and gravity when there's nothing under it.
    /// </summary>
    internal sealed partial class Horse
    {
        // Where it is: the point on the ground under its middle, which way it faces, and how it's moving (m/s, deg/s).
        private Vector3 pos;
        private float yaw, speed, yawRate, vy, pitch, airTime;
        private bool airborne;
        // Standing still on known ground: the ground is only looked at now and then.
        private bool settled;
        private float nextGroundCheck;

        // Its size (metres at scale 1): body capsule along its length, the head out in front, a rider's head above.
        private const float BodyHeight = 1.0f, BodyHalf = .72f, BodyRadius = .4f;
        private const float HeadForward = 1.12f, HeadHeight = 1.62f, HeadRadius = .28f;
        private const float RiderHeadHeight = 2.2f, RiderHeadRadius = .22f;
        private const float MaxStep = .5f, Skin = .03f, Gravity = 18f, HoofSpan = .9f;

        private bool Ridden => Riding.Horse == this;

        private void Capsule(Vector3 at, float facing, float tilt, out Vector3 front, out Vector3 back, out Vector3 head)
        {
            Quaternion q = Quaternion.Euler(tilt, facing, 0);
            Vector3 f = q * Vector3.forward, centre = at + Vector3.up * (BodyHeight * Scale);
            front = centre + f * (BodyHalf * Scale); back = centre - f * (BodyHalf * Scale);
            head = at + q * new Vector3(0, HeadHeight * Scale, HeadForward * Scale);
        }

        private bool Fits(Vector3 at, float facing, float tilt)
        {
            Capsule(at, facing, tilt, out Vector3 front, out Vector3 back, out Vector3 head);
            return !Physics.CheckCapsule(front, back, BodyRadius * Scale, mask, QueryTriggerInteraction.Ignore)
                && !Physics.CheckSphere(head, HeadRadius * Scale, mask, QueryTriggerInteraction.Ignore);
        }

        // How far the body can go along 'dir' before touching something (and what).
        private float Clear(Vector3 dir, float distance, out RaycastHit first)
        {
            Capsule(pos, yaw, pitch, out Vector3 front, out Vector3 back, out Vector3 head);
            float best = distance, reach = distance + Skin; first = default;
            if (Physics.CapsuleCast(front, back, BodyRadius * Scale, dir, out RaycastHit hit, reach, mask, QueryTriggerInteraction.Ignore))
            { best = Mathf.Max(0, hit.distance - Skin); first = hit; }
            if (Physics.SphereCast(head, HeadRadius * Scale, dir, out hit, reach, mask, QueryTriggerInteraction.Ignore) && hit.distance - Skin < best)
            { best = Mathf.Max(0, hit.distance - Skin); first = hit; }
            // With someone on, their head needs room too (no riding under a low roof or through a doorway).
            if (Ridden && Plugin.HeadRoom.Value
                && Physics.SphereCast(pos + Vector3.up * (RiderHeadHeight * Scale), RiderHeadRadius, dir, out hit, reach, mask, QueryTriggerInteraction.Ignore)
                && hit.distance - Skin < best)
            { best = Mathf.Max(0, hit.distance - Skin); first = hit; }
            return best;
        }

        private bool Ground(Vector3 at, float above, float below, out RaycastHit hit)
            => Physics.Raycast(at + Vector3.up * above, Vector3.down, out hit, above + below, mask, QueryTriggerInteraction.Ignore);

        /// <summary>
        /// One step of the horse's own motion toward a speed (m/s for a horse of normal size; negative backs up) and a
        /// turn (-1 left .. 1 right).
        /// </summary>
        private void Drive(float targetSpeed, float steer, float dt)
        {
            float scale = Scale;
            // Standing with nothing asked of it: nothing to work out but an occasional look at the ground.
            if (settled && targetSpeed == 0 && steer == 0 && Time.time < nextGroundCheck) return;
            settled = false;

            // Everything below is at the horse's own size; the settings are for a normal-sized one.
            float v = speed / scale;
            if (!airborne)
            {
                float accel = Plugin.Acceleration.Value;
                bool faster = Mathf.Abs(targetSpeed) > Mathf.Abs(v) && (Mathf.Abs(v) < .05f || Mathf.Sign(targetSpeed) == Mathf.Sign(v));
                float rate = faster ? accel * (v > Plugin.TrotSpeed.Value ? .7f : 1f) : accel * 2.2f;
                v = Mathf.MoveTowards(v, targetSpeed, rate * dt);
                speed = v * scale;
            }

            // Turning: quickest at a walk, wider at a gallop, slow on the spot, hardly at all in the air.
            float s = Mathf.Abs(v);
            float turnFactor = airborne ? .15f : s < .2f ? .65f : s < Plugin.WalkSpeed.Value * 1.3f ? 1f : s < Plugin.TrotSpeed.Value * 1.2f ? .85f : .55f;
            yawRate = Mathf.MoveTowards(yawRate, Mathf.Clamp(steer, -1, 1) * Plugin.TurnRate.Value * turnFactor, 300 * dt);
            if (Mathf.Abs(yawRate) > .01f)
            {
                float turned = yaw + yawRate * dt;
                // Not into a wall (unless it's somehow in one already, when turning is how it gets out).
                if (Fits(pos, turned, pitch) || !Fits(pos, yaw, pitch)) yaw = Mathf.Repeat(turned, 360);
                else yawRate = 0;
            }

            // Along the ground: swept, sliding along what it meets, losing the speed it hit with.
            Vector3 move = Quaternion.Euler(0, yaw, 0) * Vector3.forward * (speed * dt);
            for (int i = 0; i < 3 && move.sqrMagnitude > 1e-8f; i++)
            {
                float length = move.magnitude;
                Vector3 dir = move / length;
                float can = Clear(dir, length, out RaycastHit hit);
                Vector3 step = dir * can;
                if (!StepFits(step)) { if (i == 0) speed *= .5f; break; }
                pos += step;
                if (can >= length - 1e-5f) break;
                Vector3 n = hit.normal; n.y = 0;
                if (n.sqrMagnitude < 1e-4f) break;
                n.Normalize();
                if (i == 0) speed *= Mathf.Clamp01(1.15f + Vector3.Dot(dir, n) * 1.1f);
                move = Vector3.ProjectOnPlane(dir * (length - can), n);
            }
            Vertical(dt);
        }

        // A step onto ground more than MaxStep above where it stands, or up something too steep, is a wall.
        private bool StepFits(Vector3 step)
        {
            if (step.sqrMagnitude < 1e-8f || airborne) return true;
            if (!Ground(pos + step, (MaxStep + .3f) * Scale, 1.2f * Scale, out RaycastHit hit)) return true;
            if (hit.point.y > pos.y + MaxStep * Scale) return false;
            return !(hit.normal.y < .62f && hit.point.y > pos.y + .05f);
        }

        private void Vertical(float dt)
        {
            float scale = Scale;
            if (!airborne)
            {
                if (!Ground(pos, (MaxStep + .3f) * scale, .75f * scale, out RaycastHit hit)) { airborne = true; vy = 0; airTime = 0; }
                else
                {
                    float diff = hit.point.y - pos.y;
                    // Up and down steps are eased, so the horse (and you) don't jerk up each stair.
                    pos.y += diff * (1 - Mathf.Exp(-(Mathf.Abs(diff) > .25f ? 22f : 14f) * dt));
                    // Front and back hooves' ground tilts it on slopes.
                    Vector3 f = Quaternion.Euler(0, yaw, 0) * Vector3.forward * (HoofSpan * scale);
                    float front = Ground(pos + f, 1.2f * scale, 1.6f * scale, out RaycastHit fh) ? fh.point.y : hit.point.y;
                    float back = Ground(pos - f, 1.2f * scale, 1.6f * scale, out RaycastHit bh) ? bh.point.y : hit.point.y;
                    float wantPitch = Mathf.Clamp(Mathf.Atan2(back - front, HoofSpan * scale * 2) * Mathf.Rad2Deg, -22, 22);
                    pitch = Mathf.Lerp(pitch, wantPitch, 1 - Mathf.Exp(-8 * dt));
                    // At rest on it: nothing more to do until something's asked, bar a look now and then.
                    if (speed == 0 && yawRate == 0 && Mathf.Abs(diff) < .003f && Mathf.Abs(pitch - wantPitch) < .1f)
                    { settled = true; nextGroundCheck = Time.time + .5f; }
                }
            }
            if (!airborne) return;
            airTime += dt;
            vy -= Gravity * dt;
            float dy = vy * dt;
            if (vy > 0 && HitsAbove(dy)) { vy = 0; dy = 0; }
            if (vy <= 0 && Ground(pos, .5f * scale, .05f - dy, out RaycastHit land))
            {
                pos.y = land.point.y;
                airborne = false;
                if (vy < -11f && Ridden) Say("Hard landing.");
                vy = 0;
            }
            else pos.y += dy;
            pitch = Mathf.Lerp(pitch, Mathf.Clamp(-vy * 2.5f, -15, 15), 1 - Mathf.Exp(-6 * dt));
        }

        // Going up: its back, or its rider's head, against something overhead.
        private bool HitsAbove(float rise)
        {
            float scale = Scale;
            if (Physics.SphereCast(pos + Vector3.up * (BodyHeight * scale), BodyRadius * scale, Vector3.up, out _, rise + .05f, mask, QueryTriggerInteraction.Ignore)) return true;
            return Ridden && Plugin.HeadRoom.Value
                && Physics.SphereCast(pos + Vector3.up * (RiderHeadHeight * scale), RiderHeadRadius, Vector3.up, out _, rise + .05f, mask, QueryTriggerInteraction.Ignore);
        }

        private void TryJump()
        {
            if (airborne || jumping) return;
            jumping = true; fell = landing = false;
            airborne = true; airTime = 0; settled = false;
            vy = Mathf.Sqrt(2 * Gravity * Plugin.JumpHeight.Value * Scale);
            // A standing jump is a small hop forward.
            speed = Mathf.Max(speed, 2f * Scale);
            model.NextBlend(.12f);
            model.Restart();
        }
    }
}
