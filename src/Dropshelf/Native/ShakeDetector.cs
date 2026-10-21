using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>
/// Detects a "shake" of the cursor: several quick direction reversals along either axis,
/// each travelling at least a minimum distance, inside a short time window.
/// </summary>
public sealed class ShakeDetector
{
    private readonly AxisTracker _x = new();
    private readonly AxisTracker _y = new();

    private int _minSegment = 70;
    private int _requiredReversals = 4;
    private uint _windowMs = 1000;

    /// <param name="scale">Monitor DPI scale, so the gesture feels the same on every display.</param>
    public void Configure(ShakeSensitivity sensitivity, double scale)
    {
        (int segment, int reversals) = sensitivity switch
        {
            ShakeSensitivity.High => (50, 3),
            ShakeSensitivity.Highest => (32, 2),
            _ => (70, 4),
        };
        _minSegment = Math.Max(8, (int)Math.Round(segment * scale));
        _requiredReversals = reversals;
        _windowMs = 1000;
    }

    public void Reset()
    {
        _x.Reset();
        _y.Reset();
    }

    /// <summary>Feeds one cursor sample; returns true when a shake completes.</summary>
    public bool Add(int x, int y, uint timeMs)
    {
        var shakeX = _x.Add(x, timeMs, this);
        var shakeY = _y.Add(y, timeMs, this);
        return shakeX || shakeY;
    }

    private sealed class AxisTracker
    {
        private readonly Queue<uint> _reversals = new();
        private bool _initialized;
        private int _direction;
        private int _extreme;

        public void Reset()
        {
            _initialized = false;
            _direction = 0;
            _reversals.Clear();
        }

        public bool Add(int value, uint time, ShakeDetector p)
        {
            if (!_initialized)
            {
                _initialized = true;
                _extreme = value;
                return false;
            }

            var delta = value - _extreme;
            if (_direction == 0)
            {
                if (Math.Abs(delta) >= p._minSegment / 2)
                {
                    _direction = Math.Sign(delta);
                    _extreme = value;
                }
                return false;
            }

            if (delta == 0 || Math.Sign(delta) == _direction)
            {
                // Still travelling the same way: push the turning point further out.
                _extreme = value;
                return false;
            }

            if (Math.Abs(delta) < p._minSegment) return false;

            _direction = -_direction;
            _extreme = value;
            _reversals.Enqueue(time);
            while (_reversals.Count > 0 && time - _reversals.Peek() > p._windowMs) _reversals.Dequeue();
            return _reversals.Count >= p._requiredReversals;
        }
    }
}
