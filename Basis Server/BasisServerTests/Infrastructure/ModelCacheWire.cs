using Basis.Network.Core;
using System.Text;

namespace BasisServerTests;

/// <summary>
/// Byte-for-byte what the model pickup manager puts on the wire, plus readers for what the server
/// sends back. Shared by the model cache, governor and relay tests so every one of them walks the
/// variable-length owner name for real.
/// </summary>
internal static class ModelCacheWire
{
    public const byte OpSpawn = 1;
    public const byte OpChunk = 2;
    public const byte OpTransform = 3;
    public const byte OpDespawn = 4;
    public const byte OpClaim = 5;
    public const byte OpAnimationSpawn = 6;
    public const byte OpAnimationChunk = 7;
    public const byte OpServerCacheState = 8;
    public const byte OpServerCacheOffer = 9;
    public const byte OpServerCacheRequest = 10;
    public const byte OpHello = 11;

    /// <summary>u16 playerID + u16 messageIndex in front of every server scene send.</summary>
    public const int ServerEnvelopeBytes = 4;

    public static byte[] EncodeSpawn(
        Guid id,
        ushort owner,
        string name,
        int a,
        int b,
        int totalBytes,
        int totalChunks,
        float px,
        float py,
        float pz,
        float rx,
        float ry,
        float rz,
        float rw,
        byte[]? tail = null,
        byte op = OpSpawn
    )
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(op);
        writer.Write(id.ToByteArray());
        writer.Write(owner);
        writer.Write(name);
        writer.Write(a);
        writer.Write(b);
        writer.Write(totalBytes);
        writer.Write(totalChunks);
        writer.Write(px);
        writer.Write(py);
        writer.Write(pz);
        writer.Write(rx);
        writer.Write(ry);
        writer.Write(rz);
        writer.Write(rw);
        if (tail != null)
        {
            writer.Write(tail);
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] EncodeChunk(Guid id, int index, int bytes, byte op = OpChunk)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(op);
        writer.Write(id.ToByteArray());
        writer.Write(index);
        writer.Write(bytes);
        writer.Write(new byte[bytes]);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Rewrites the chunk index of an encoded chunk in place, so one buffer can stand in for many.</summary>
    public static void SetChunkIndex(byte[] chunk, int index) => BitConverter.TryWriteBytes(chunk.AsSpan(17, 4), index);

    public static byte[] EncodeTransform(
        Guid id,
        float px,
        float py,
        float pz,
        float rx = 0f,
        float ry = 0f,
        float rz = 0f,
        float rw = 1f,
        float scale = 1f
    )
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(OpTransform);
        writer.Write(id.ToByteArray());
        writer.Write(px);
        writer.Write(py);
        writer.Write(pz);
        writer.Write(rx);
        writer.Write(ry);
        writer.Write(rz);
        writer.Write(rw);
        writer.Write(scale);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] EncodeOpGuid(byte op, Guid id)
    {
        byte[] payload = new byte[17];
        payload[0] = op;
        id.TryWriteBytes(payload.AsSpan(1));
        return payload;
    }

    public static byte[] PayloadOf(byte[] sent) => sent.AsSpan(ServerEnvelopeBytes).ToArray();

    public static byte PayloadOpcode(byte[] sent) => sent[ServerEnvelopeBytes];

    public static ushort PlayerIdOf(byte[] sent) => BitConverter.ToUInt16(sent, 0);

    public static ushort MessageIndexOf(byte[] sent) => BitConverter.ToUInt16(sent, 2);

    public static Guid GuidOf(byte[] payload) => new Guid(payload.AsSpan(1, 16));

    /// <summary>Walks a spawn header or an offer the way the client does and returns the seven pose floats.</summary>
    public static (float X, float Y, float Z, float RX, float RY, float RZ, float RW) ReadSpawnPose(byte[] header)
    {
        using MemoryStream stream = new MemoryStream(header, false);
        using BinaryReader reader = SkipToPose(stream);
        return (
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle()
        );
    }

    /// <summary>Everything after the pose: the model's claims tail, which the server must never touch.</summary>
    public static byte[] TailOf(byte[] header)
    {
        using MemoryStream stream = new MemoryStream(header, false);
        using BinaryReader reader = SkipToPose(stream);
        reader.ReadBytes(28);
        return reader.ReadBytes((int)(stream.Length - stream.Position));
    }

    private static BinaryReader SkipToPose(MemoryStream stream)
    {
        BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        reader.ReadByte();
        reader.ReadBytes(16);
        reader.ReadUInt16();
        reader.ReadString();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        return reader;
    }

    /// <summary>A client-to-server scene message as HandleScene reads it.</summary>
    public static NetPacketReader ScenePacket(ushort messageIndex, byte[] payload, ushort[]? recipients = null)
    {
        NetDataWriter writer = new NetDataWriter();
        writer.Put(messageIndex);
        ushort count = (ushort)(recipients?.Length ?? 0);
        writer.Put(count);
        for (int index = 0; index < count; index++)
        {
            writer.Put(recipients![index]);
        }
        writer.Put(payload);
        byte[] bytes = writer.AsReadOnlySpan().ToArray();
        return NetPacketReader.Create(bytes, 0, bytes.Length, () => { });
    }
}
