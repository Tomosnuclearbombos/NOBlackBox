using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NOBlackBox
{
    // Tomo's Nukes performance build (2026-10-12): the properties of one ACMI line. It replaces the Dictionary<string,
    // string> every recorded object used to fill, and keeps the same rules (Add throws on a key that is already there,
    // setting a key keeps its place, lines list the keys in the order they were added). The difference: a number is kept
    // as a number, with the format it will be written in, and the writer thread turns it into text. Turning positions and
    // telemetry into text was the main thread's biggest share of the actual recording work.
    internal sealed class PropBag : IEnumerable<KeyValuePair<string, string>>
    {
        internal const byte Str = 0;        // a ready string (null writes nothing, as before)
        internal const byte Num2 = 1;       // ToString("0.##", culture)
        internal const byte Num3 = 2;       // ToString("0.###", culture)
        internal const byte Pose9 = 3;      // a unit's T=lon|lat|alt|roll|pitch|yaw|x|z|yaw, written where it changed
        internal const byte Pose5 = 4;      // a gun round's or flare's T=lon|lat|alt|x|z, written where it changed

        internal int count;
        internal string[] keys;
        internal string?[] strs;
        internal byte[] kinds;
        internal CultureInfo?[] cultures;
        internal float[] nums;   // 12 per entry: a pose is new and last position and rotation
#if SHADOW
        internal string?[] expect;  // check build: the text the old code built for entry i
#endif

        // a gun round or flare line has two entries; a unit line can have twenty (it grows when it needs to)
        public PropBag() : this(16) { }

        public PropBag(int capacity)
        {
            capacity = Math.Max(2, capacity);
            keys = new string[capacity];
            strs = new string?[capacity];
            kinds = new byte[capacity];
            cultures = new CultureInfo?[capacity];
            nums = new float[capacity * 12];
#if SHADOW
            expect = new string?[capacity];
#endif
        }

        public int Count => count;

        public void Clear()
        {
            Array.Clear(strs, 0, count);
            Array.Clear(cultures, 0, count);
#if SHADOW
            Array.Clear(expect, 0, count);
#endif
            count = 0;
        }

        private int Find(string key)
        {
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(keys[i], key))
                {
                    return i;
                }
            }
            return -1;
        }

        private int Append(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (Find(key) >= 0)
            {
                throw new ArgumentException("An item with the same key has already been added. Key: " + key);
            }
            if (count == keys.Length)
            {
                int grow = Math.Max(4, count * 2);
                Array.Resize(ref keys, grow);
                Array.Resize(ref strs, grow);
                Array.Resize(ref kinds, grow);
                Array.Resize(ref cultures, grow);
                Array.Resize(ref nums, grow * 12);
#if SHADOW
                Array.Resize(ref expect, grow);
#endif
            }
            keys[count] = key;
            return count++;
        }

        public void Add(string key, string? value)
        {
            int i = Append(key);
            strs[i] = value;
            kinds[i] = Str;
        }

        public string? this[string key]
        {
            get
            {
                int i = Find(key);
                if (i < 0)
                {
                    throw new KeyNotFoundException(key);
                }
                return kinds[i] == Str ? strs[i] : Text(i);
            }
            set
            {
                int i = Find(key);
                if (i < 0)
                {
                    i = Append(key);
                }
                strs[i] = value;
                kinds[i] = Str;
                cultures[i] = null;
            }
        }

        // a number written later as value.ToString(format, culture), format "0.##" (Num2) or "0.###" (Num3)
        public void AddNum(string key, float value, byte kind, CultureInfo culture)
        {
            int i = Append(key);
            kinds[i] = kind;
            cultures[i] = culture;
            nums[i * 12] = value;
        }

        public void AddPose9(string key, Vector3 newPos, Vector3 newRot, Vector3 lastPos, Vector3 lastRot)
        {
            int i = Append(key);
            kinds[i] = Pose9;
            int b = i * 12;
            nums[b] = newPos.x; nums[b + 1] = newPos.y; nums[b + 2] = newPos.z;
            nums[b + 3] = newRot.x; nums[b + 4] = newRot.y; nums[b + 5] = newRot.z;
            nums[b + 6] = lastPos.x; nums[b + 7] = lastPos.y; nums[b + 8] = lastPos.z;
            nums[b + 9] = lastRot.x; nums[b + 10] = lastRot.y; nums[b + 11] = lastRot.z;
        }

        public void AddPose5(string key, Vector3 newPos, Vector3 lastPos)
        {
            int i = Append(key);
            kinds[i] = Pose5;
            int b = i * 12;
            nums[b] = newPos.x; nums[b + 1] = newPos.y; nums[b + 2] = newPos.z;
            nums[b + 6] = lastPos.x; nums[b + 7] = lastPos.y; nums[b + 8] = lastPos.z;
        }

        // a copy for the writer thread (the object clears its own bag straight after)
        internal void CopyFrom(PropBag src)
        {
            Clear();
            if (keys.Length < src.count)
            {
                Array.Resize(ref keys, src.keys.Length);
                Array.Resize(ref strs, src.keys.Length);
                Array.Resize(ref kinds, src.keys.Length);
                Array.Resize(ref cultures, src.keys.Length);
                Array.Resize(ref nums, src.keys.Length * 12);
#if SHADOW
                Array.Resize(ref expect, src.keys.Length);
#endif
            }
            count = src.count;
            Array.Copy(src.keys, keys, count);
            Array.Copy(src.strs, strs, count);
            Array.Copy(src.kinds, kinds, count);
            Array.Copy(src.cultures, cultures, count);
            Array.Copy(src.nums, nums, count * 12);
#if SHADOW
            Array.Copy(src.expect, expect, count);
#endif
        }

        // the text of entry i, exactly as the old code built it on the main thread
        internal string? Text(int i)
        {
            int b = i * 12;
            switch (kinds[i])
            {
                case Num2:
                    return nums[b].ToString("0.##", cultures[i]);
                case Num3:
                    return nums[b].ToString("0.###", cultures[i]);
                case Pose9:
                    return PoseText.Unit(new Vector3(nums[b], nums[b + 1], nums[b + 2]), new Vector3(nums[b + 3], nums[b + 4], nums[b + 5]),
                        new Vector3(nums[b + 6], nums[b + 7], nums[b + 8]), new Vector3(nums[b + 9], nums[b + 10], nums[b + 11]));
                case Pose5:
                    return PoseText.Point(new Vector3(nums[b], nums[b + 1], nums[b + 2]), new Vector3(nums[b + 6], nums[b + 7], nums[b + 8]));
                default:
                    return strs[i];
            }
        }

        // "<id>,<key>=<value>,..." (the same text ACMIWriter.StringifyProps built)
        internal void AppendLine(StringBuilder sb, long tacviewId)
        {
            sb.Append(tacviewId.ToString("X")).Append(',');
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(keys[i]).Append('=').Append(Text(i));
            }
        }

        // for the collection initialisers in the objects' Init and for anything that lists the props
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                yield return new KeyValuePair<string, string>(keys[i], Text(i)!);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // The position text, moved here unchanged from ACMIUnit_mono.UpdatePosition and the gun round / flare UpdatePosition
    // so the writer thread can build it. Only reads its arguments.
    internal static class PoseText
    {
        internal static string Unit(Vector3 newPos, Vector3 newRot, Vector3 lastPos, Vector3 lastRot)
        {
            string x = Mathf.Approximately(newPos.x, lastPos.x) ? "" : newPos.x.ToString(CultureInfo.InvariantCulture);
            string y = Mathf.Approximately(newPos.y, lastPos.y) ? "" : newPos.y.ToString(CultureInfo.InvariantCulture);
            string z = Mathf.Approximately(newPos.z, lastPos.z) ? "" : newPos.z.ToString(CultureInfo.InvariantCulture);

            float adjusted_roll = newRot.z > 180.0f ? 360 - newRot.z : -newRot.z;
            float adjusted_pitch = newRot.x > 180.0f ? 360 - newRot.x : -newRot.x;
            float adjusted_yaw = newRot.y;

            string roll = Mathf.Approximately(newRot.z, lastRot.z) ? "" : adjusted_roll.ToString(CultureInfo.InvariantCulture);
            string pitch = Mathf.Approximately(newRot.x, lastRot.x) ? "" : adjusted_pitch.ToString(CultureInfo.InvariantCulture);
            string yaw = Mathf.Approximately(newRot.y, lastRot.y) ? "" : adjusted_yaw.ToString(CultureInfo.InvariantCulture);

            (float latitude, float longitude) = Helpers.CartesianToGeodetic(newPos.x, newPos.z);

            return $"{(newPos.x != lastPos.x ? longitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{(newPos.z != lastPos.z ? latitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{y}|{roll}|{pitch}|{yaw}|{x}|{z}|{yaw}";
        }

        internal static string Point(Vector3 newPos, Vector3 lastPos)
        {
            string x = Mathf.Approximately(newPos.x, lastPos.x) ? "" : newPos.x.ToString(CultureInfo.InvariantCulture);
            string y = Mathf.Approximately(newPos.y, lastPos.y) ? "" : newPos.y.ToString(CultureInfo.InvariantCulture);
            string z = Mathf.Approximately(newPos.z, lastPos.z) ? "" : newPos.z.ToString(CultureInfo.InvariantCulture);
            (float latitude, float longitude) = Helpers.CartesianToGeodetic(newPos.x, newPos.z);
            return $"{(newPos.x != lastPos.x ? longitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{(newPos.z != lastPos.z ? latitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{y}|{x}|{z}";
        }
    }
}
