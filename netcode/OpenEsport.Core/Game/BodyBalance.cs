using System;

namespace OpenEsport.Core
{
    public static class BodyBalance
    {
        // Neutral is encoded as 128. Facial features, ancestry and clothing are cosmetic.
        private static float Height(byte[] look) => look == null || look.Length < 5 ? 0 :
            (look[4] - 128) / (look[4] < 128 ? 128f : 127f);

        public static float Speed(byte[] look) => 1 + .03f * Height(look) * GameBalance.BodyBalanceStrength;

        public static float BulletDamage(byte[] look, Vec3 velocity)
        {
            float height = Height(look);
            float stride = Math.Clamp(MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z) /
                MathF.Max(.001f, Sim.BaseSpeed * Sim.SprintMultiplier), 0, 1);
            // A small target is slightly more vulnerable. Fast travel costs protection,
            // especially at the tall end, so neither extreme wins on both axes.
            float exposure = .015f * MathF.Max(0, -height) + .02f * stride * (1 + MathF.Max(0,height));
            return 1 + exposure * GameBalance.BodyBalanceStrength;
        }
    }
}
