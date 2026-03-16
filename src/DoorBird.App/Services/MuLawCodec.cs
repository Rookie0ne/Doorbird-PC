namespace DoorBird.App.Services;

/// <summary>
/// G.711 mu-law encoder/decoder for DoorBird audio (8 kHz, mono).
/// </summary>
public static class MuLawCodec {
    private const int Bias = 0x84;
    private const int Max = 0x7FFF;

    private static readonly int[] ExpTable = { 0, 132, 396, 924, 1980, 4092, 8316, 16764 };

    public static byte Encode(short sample) {
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > Max) sample = Max;

        sample = (short)(sample + Bias);
        int exponent = 7;
        for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte muLaw = (byte)(~(sign | (exponent << 4) | mantissa));
        return muLaw;
    }

    public static short Decode(byte muLaw) {
        muLaw = (byte)~muLaw;
        int sign = muLaw & 0x80;
        int exponent = (muLaw >> 4) & 0x07;
        int data = ExpTable[exponent] + ((muLaw & 0x0F) << (exponent + 3));
        return (short)(sign != 0 ? -data : data);
    }

    public static byte[] EncodePcm(byte[] pcm16, int offset, int count) {
        int sampleCount = count / 2;
        var encoded = new byte[sampleCount];
        for (int i = 0; i < sampleCount; i++) {
            short sample = (short)(pcm16[offset + i * 2] | (pcm16[offset + i * 2 + 1] << 8));
            encoded[i] = Encode(sample);
        }
        return encoded;
    }

    public static byte[] DecodeToPcm(byte[] muLawData, int offset, int count) {
        var pcm = new byte[count * 2];
        for (int i = 0; i < count; i++) {
            short sample = Decode(muLawData[offset + i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcm;
    }
}
