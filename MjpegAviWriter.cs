using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PrefireMacro
{
    // RIFF AVI 1.0 with independent JPEG frames. Segments stay well below its 2 GB limit.
    internal sealed class MjpegAviWriter : IDisposable
    {
        readonly BinaryWriter writer;
        readonly List<uint> offsets = new List<uint>(), lengths = new List<uint>();
        readonly long riffSize, totalFrames, streamFrames, moviSize, moviBase;
        bool closed;
        internal int FrameCount { get { return offsets.Count; } }
        internal long Length { get { return writer.BaseStream.Position; } }
        static void Four(BinaryWriter w, string s) { w.Write(Encoding.ASCII.GetBytes(s)); }
        void Patch(long at, uint value) { long end = writer.BaseStream.Position; writer.BaseStream.Position = at; writer.Write(value); writer.BaseStream.Position = end; }
        internal MjpegAviWriter(string path, int width, int height, int fps)
        {
            writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
            Four(writer, "RIFF"); riffSize = writer.BaseStream.Position; writer.Write(0u); Four(writer, "AVI ");
            Four(writer, "LIST"); writer.Write(192u); Four(writer, "hdrl");
            Four(writer, "avih"); writer.Write(56u);
            writer.Write(1000000u / (uint)fps); writer.Write(0u); writer.Write(0u); writer.Write(0x10u);
            totalFrames = writer.BaseStream.Position; writer.Write(0u); writer.Write(0u); writer.Write(1u); writer.Write((uint)(width * height * 3));
            writer.Write((uint)width); writer.Write((uint)height); for (int i = 0; i < 4; i++) writer.Write(0u);
            Four(writer, "LIST"); writer.Write(116u); Four(writer, "strl");
            Four(writer, "strh"); writer.Write(56u); Four(writer, "vids"); Four(writer, "MJPG");
            writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u);
            writer.Write(1u); writer.Write((uint)fps); writer.Write(0u);
            streamFrames = writer.BaseStream.Position; writer.Write(0u); writer.Write((uint)(width * height * 3)); writer.Write(0xffffffffu); writer.Write(0u);
            writer.Write((short)0); writer.Write((short)0); writer.Write((short)width); writer.Write((short)height);
            Four(writer, "strf"); writer.Write(40u); writer.Write(40u); writer.Write(width); writer.Write(height);
            writer.Write((ushort)1); writer.Write((ushort)24); Four(writer, "MJPG"); writer.Write((uint)(width * height * 3));
            writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
            Four(writer, "LIST"); moviSize = writer.BaseStream.Position; writer.Write(0u); moviBase = writer.BaseStream.Position; Four(writer, "movi");
        }
        internal void Add(byte[] jpeg)
        {
            if (closed) throw new ObjectDisposedException("MjpegAviWriter");
            offsets.Add((uint)(writer.BaseStream.Position - moviBase)); lengths.Add((uint)jpeg.Length);
            Four(writer, "00dc"); writer.Write((uint)jpeg.Length); writer.Write(jpeg); if ((jpeg.Length & 1) != 0) writer.Write((byte)0);
        }
        public void Dispose()
        {
            if (closed) return; closed = true;
            try
            {
                Patch(moviSize, (uint)(writer.BaseStream.Position - moviSize - 4));
                Four(writer, "idx1"); writer.Write((uint)(offsets.Count * 16));
                for (int i = 0; i < offsets.Count; i++) { Four(writer, "00dc"); writer.Write(0x10u); writer.Write(offsets[i]); writer.Write(lengths[i]); }
                Patch(totalFrames, (uint)offsets.Count); Patch(streamFrames, (uint)offsets.Count); Patch(riffSize, (uint)(writer.BaseStream.Length - 8));
            }
            finally { writer.Dispose(); }
        }
    }
}
