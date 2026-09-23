namespace StayView;

// Critically damped spring (implicit Euler; stable for any frame time). Used for hover
// zooms: it glides smoothly in both directions, never overshoots, and keeps its velocity
// when the target flips mid-flight, so reversing a half-finished zoom has no jolt. A fixed
// easing curve run backwards starts slowly and snaps at the end instead.
struct Spring
{
    public double Value, Velocity;
    // omega (rad/s) sets the pace; ~16 settles in roughly 0.3 s.
    public void Step(double target, double seconds, double omega = 16)
    {
        double dt = Math.Clamp(seconds, 0, .05);
        double f = 1 + 2 * dt * omega, oo = omega * omega, hoo = dt * oo, hhoo = dt * hoo;
        double inv = 1 / (f + hhoo);
        double x = (f * Value + dt * Velocity + hhoo * target) * inv;
        double v = (Velocity + hoo * (target - Value)) * inv;
        Value = x; Velocity = v;
    }
    public bool Settled(double target) => Math.Abs(Value - target) < .002 && Math.Abs(Velocity) < .02;
    public void Reset() { Value = 0; Velocity = 0; }
}
