using UnityEngine;
using P = Core.Input.InputButtonProcessor;
using CI = Core.Input;

namespace OriCoop
{
    // A full copy of Core.Input's static state. Swap() exchanges this object's values with the
    // live statics, so calling it twice restores the original. Hand-written field access: this
    // runs for every P2 component callback, so no reflection.
    public class InputState
    {
        public float Horizontal, Vertical, HorizontalAnalogLeft, VerticalAnalogLeft, HorizontalAnalogRight, VerticalAnalogRight;
        public int HorizontalDigiPad, VerticalDigiPad;
        public Vector2 CursorPosition;
        public bool CursorMoved;
        public P Down = new P(), Up = new P(), Left = new P(), Right = new P(), Jump = new P(), SpiritFlame = new P(),
            Bash = new P(), SoulFlame = new P(), ChargeJump = new P(), Glide = new P(), Grab = new P(), ZoomIn = new P(),
            ZoomOut = new P(), LeftShoulder = new P(), RightShoulder = new P(), Start = new P(), AnyStart = new P(),
            Select = new P(), Unassigned = new P(), LeftStick = new P(), RightStick = new P(), MenuDown = new P(),
            MenuUp = new P(), MenuLeft = new P(), MenuRight = new P(), MenuPageLeft = new P(), MenuPageRight = new P(),
            ActionButtonA = new P(), Cancel = new P(), LeftClick = new P(), RightClick = new P(), Copy = new P(),
            Delete = new P(), Focus = new P(), Filter = new P(), Legend = new P();
        public P[] Buttons;

        public InputState()
        {
            // Same order as Core.Input.Buttons.
            Buttons = new P[] { Down, Up, Left, Right, Jump, SpiritFlame, Bash, SoulFlame, ChargeJump, Glide,
                Grab, LeftShoulder, RightShoulder, Start, AnyStart, Select, LeftStick, RightStick, MenuDown, MenuUp,
                MenuLeft, MenuRight, MenuPageLeft, MenuPageRight, ActionButtonA, ZoomIn, ZoomOut, Cancel, Copy, Delete,
                Focus, Filter, Legend };
        }

        static void S<T>(ref T a, ref T b) { T t = a; a = b; b = t; }

        public void Swap()
        {
            S(ref Horizontal, ref CI.Horizontal); S(ref Vertical, ref CI.Vertical);
            S(ref HorizontalDigiPad, ref CI.HorizontalDigiPad); S(ref VerticalDigiPad, ref CI.VerticalDigiPad);
            S(ref HorizontalAnalogLeft, ref CI.HorizontalAnalogLeft); S(ref VerticalAnalogLeft, ref CI.VerticalAnalogLeft);
            S(ref HorizontalAnalogRight, ref CI.HorizontalAnalogRight); S(ref VerticalAnalogRight, ref CI.VerticalAnalogRight);
            S(ref CursorPosition, ref CI.CursorPosition); S(ref CursorMoved, ref CI.CursorMoved);
            S(ref Down, ref CI.Down); S(ref Up, ref CI.Up); S(ref Left, ref CI.Left); S(ref Right, ref CI.Right);
            S(ref Jump, ref CI.Jump); S(ref SpiritFlame, ref CI.SpiritFlame); S(ref Bash, ref CI.Bash);
            S(ref SoulFlame, ref CI.SoulFlame); S(ref ChargeJump, ref CI.ChargeJump); S(ref Glide, ref CI.Glide);
            S(ref Grab, ref CI.Grab); S(ref ZoomIn, ref CI.ZoomIn); S(ref ZoomOut, ref CI.ZoomOut);
            S(ref LeftShoulder, ref CI.LeftShoulder); S(ref RightShoulder, ref CI.RightShoulder);
            S(ref Start, ref CI.Start); S(ref AnyStart, ref CI.AnyStart); S(ref Select, ref CI.Select);
            S(ref Unassigned, ref CI.Unassigned); S(ref LeftStick, ref CI.LeftStick); S(ref RightStick, ref CI.RightStick);
            S(ref MenuDown, ref CI.MenuDown); S(ref MenuUp, ref CI.MenuUp); S(ref MenuLeft, ref CI.MenuLeft);
            S(ref MenuRight, ref CI.MenuRight); S(ref MenuPageLeft, ref CI.MenuPageLeft); S(ref MenuPageRight, ref CI.MenuPageRight);
            S(ref ActionButtonA, ref CI.ActionButtonA); S(ref Cancel, ref CI.Cancel); S(ref LeftClick, ref CI.LeftClick);
            S(ref RightClick, ref CI.RightClick); S(ref Copy, ref CI.Copy); S(ref Delete, ref CI.Delete);
            S(ref Focus, ref CI.Focus); S(ref Filter, ref CI.Filter); S(ref Legend, ref CI.Legend);
            S(ref Buttons, ref CI.Buttons);
        }
    }
}
