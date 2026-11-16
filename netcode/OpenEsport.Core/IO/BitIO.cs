using System;
using System.Text;

namespace OpenEsport.Core
{
	/// <summary>Écriture de bits dans un tampon d'octets (format compact des paquets).</summary>
	public sealed class BitWriter
	{
		private byte[] _buf;
		private int _bitPos;

		public BitWriter(int capacity = 1400) { _buf = new byte[capacity]; }

		public byte[] Buffer => _buf;
		public int BitLength => _bitPos;
		public int ByteLength => (_bitPos + 7) >> 3;

		public void Reset() { _bitPos = 0; }

		private void Ensure(int extraBits)
		{
			int needed = ((_bitPos + extraBits) >> 3) + 1;
			if (needed > _buf.Length)
				Array.Resize(ref _buf, Math.Max(needed, _buf.Length * 2));
		}

		/// <summary>Écrit les <paramref name="bits"/> bits de poids faible de <paramref name="value"/> (1 à 32).</summary>
		public void WriteBits(uint value, int bits)
		{
			Ensure(bits);
			while (bits > 0)
			{
				int idx = _bitPos >> 3;
				int off = _bitPos & 7;
				if (off == 0)
					_buf[idx] = 0;
				int take = Math.Min(8 - off, bits);
				uint mask = (1u << take) - 1u;
				_buf[idx] |= (byte)((value & mask) << off);
				value >>= take;
				bits -= take;
				_bitPos += take;
			}
		}

		public void WriteBool(bool v) => WriteBits(v ? 1u : 0u, 1);
		public void WriteByte(byte v) => WriteBits(v, 8);
		public void WriteUInt16(ushort v) => WriteBits(v, 16);
		public void WriteUInt32(uint v) => WriteBits(v, 32);
		public void WriteSByte(sbyte v) => WriteBits((byte)v, 8);
		public void WriteInt16(short v) => WriteBits((ushort)v, 16);

		public void WriteFloat(float v)
		{
			WriteBits(unchecked((uint)BitConverter.SingleToInt32Bits(v)), 32);
		}

		public void WriteVec3(Vec3 v)
		{
			WriteFloat(v.X); WriteFloat(v.Y); WriteFloat(v.Z);
		}

		public void WriteString(string s, int maxLength = 32)
		{
			s ??= "";
			var bytes = Encoding.UTF8.GetBytes(s.Length > maxLength ? s.Substring(0, maxLength) : s);
			int len = Math.Min(bytes.Length, 255);
			WriteByte((byte)len);
			for (int i = 0; i < len; i++)
				WriteByte(bytes[i]);
		}
	}

	/// <summary>Lecture de bits. Ne lève jamais d'exception : un paquet tronqué ou malveillant met simplement <see cref="Overflow"/> à vrai.</summary>
	public sealed class BitReader
	{
		private byte[] _buf;
		private int _bitPos;
		private int _bitLen;

		public bool Overflow { get; private set; }
		public int BitsRemaining => _bitLen - _bitPos;

		public BitReader() { }

		public BitReader(byte[] buf, int offset, int length) { Init(buf, offset, length); }

		public void Init(byte[] buf, int offset, int length)
		{
			_buf = buf;
			_bitPos = offset * 8;
			_bitLen = (offset + length) * 8;
			Overflow = false;
		}

		public uint ReadBits(int bits)
		{
			if (bits <= 0) return 0;
			if (_bitPos + bits > _bitLen)
			{
				Overflow = true;
				_bitPos = _bitLen;
				return 0;
			}
			uint result = 0;
			int shift = 0;
			while (bits > 0)
			{
				int idx = _bitPos >> 3;
				int off = _bitPos & 7;
				int take = Math.Min(8 - off, bits);
				uint mask = (1u << take) - 1u;
				result |= (((uint)_buf[idx] >> off) & mask) << shift;
				shift += take;
				bits -= take;
				_bitPos += take;
			}
			return result;
		}

		public bool ReadBool() => ReadBits(1) != 0;
		public byte ReadByte() => (byte)ReadBits(8);
		public ushort ReadUInt16() => (ushort)ReadBits(16);
		public uint ReadUInt32() => ReadBits(32);
		public sbyte ReadSByte() => (sbyte)ReadBits(8);
		public short ReadInt16() => (short)ReadBits(16);

		public float ReadFloat() => BitConverter.Int32BitsToSingle(unchecked((int)ReadBits(32)));

		public Vec3 ReadVec3() => new Vec3(ReadFloat(), ReadFloat(), ReadFloat());

		public string ReadString()
		{
			int len = ReadByte();
			if (Overflow || len * 8 > BitsRemaining)
			{
				Overflow = true;
				return "";
			}
			var bytes = new byte[len];
			for (int i = 0; i < len; i++)
				bytes[i] = ReadByte();
			return Encoding.UTF8.GetString(bytes);
		}
	}
}
