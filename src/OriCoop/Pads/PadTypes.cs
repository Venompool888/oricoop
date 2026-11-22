namespace OriCoop.Pads
{
    public enum PadFamily { None, PlayStation, Xbox }

    public struct PadSnapshot
    {
        public ushort Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
    }
}
