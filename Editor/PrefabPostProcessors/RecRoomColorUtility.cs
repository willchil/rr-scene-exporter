using UnityEngine;
using RecRoom.Protobuf;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Decodes Rec Room's SandboxColorableData color field.
    /// The color is a 32-bit int: low byte (0-255) is a palette index,
    /// the upper three bytes encode custom RGB when non-zero.
    /// </summary>
    internal static class RecRoomColorUtility
    {
        // Rec Room color palette (indices 0–60). Indices beyond this range fall back to white.
        private static readonly Color32[] Palette = new Color32[]
        {
            HexToColor32(0xD3, 0x17, 0x18), // 0  Red
            HexToColor32(0xF5, 0x5C, 0x1A), // 1  Orange
            HexToColor32(0xF5, 0xC4, 0x1F), // 2  Yellow
            HexToColor32(0x89, 0xB1, 0x51), // 3  Sage
            HexToColor32(0x17, 0x6B, 0xDD), // 4  Blue
            HexToColor32(0x34, 0x56, 0x52), // 5  Cyan
            HexToColor32(0x00, 0x9B, 0x89), // 6  Teal
            HexToColor32(0x69, 0xA2, 0x18), // 7  Green
            HexToColor32(0x56, 0x48, 0x78), // 8  Purple
            HexToColor32(0x56, 0x48, 0x78), // 9  Rose
            HexToColor32(0xEB, 0x2E, 0x50), // 10 Pink
            HexToColor32(0x5A, 0x3F, 0x31), // 11 Brown
            HexToColor32(0x25, 0x10, 0x07), // 12 Tawny
            HexToColor32(0xF6, 0xEE, 0xE8), // 13 White
            HexToColor32(0x7C, 0x78, 0x77), // 14 Gray
            HexToColor32(0x2C, 0x2E, 0x32), // 15 Charcoal
            HexToColor32(0x3D, 0x1D, 0x0D), // 16 Mocha
            HexToColor32(0x7D, 0x40, 0x19), // 17 Caramel
            HexToColor32(0xBF, 0xBC, 0xBA), // 18 Mist
            HexToColor32(0x19, 0x17, 0x17), // 19 Black
            HexToColor32(0x76, 0x08, 0x08), // 20 Garnet
            HexToColor32(0xC1, 0x37, 0x0A), // 21 Topaz
            HexToColor32(0xF5, 0xC4, 0x1F), // 22 Amber
            HexToColor32(0x69, 0xA2, 0x18), // 23 Emerald
            HexToColor32(0x00, 0x50, 0x48), // 24 Turquoise
            HexToColor32(0x06, 0x39, 0x80), // 25 Lapis
            HexToColor32(0x56, 0x48, 0x78), // 26 Amethyst
            HexToColor32(0xEB, 0x2E, 0x50), // 27 Ruby
            HexToColor32(0xE5, 0x50, 0x50), // 28 Salmon
            HexToColor32(0xF0, 0x7F, 0x4F), // 29 Cantaloupe
            HexToColor32(0xF7, 0xD7, 0x69), // 30 Pineapple
            HexToColor32(0x67, 0xDA, 0xCD), // 31 Hydrangea
            HexToColor32(0x56, 0x48, 0x78), // 32 Lavender
            HexToColor32(0xD3, 0x17, 0x18), // 33 Red
            HexToColor32(0x7C, 0x2F, 0x2F), // 34 Venetian
            HexToColor32(0x7E, 0x42, 0x2E), // 35 Sienna
            HexToColor32(0x82, 0x61, 0x38), // 36 Ochre
            HexToColor32(0x69, 0xA2, 0x18), // 37 Terre Verte
            HexToColor32(0x69, 0xA2, 0x18), // 38 Veridian
            HexToColor32(0x34, 0x56, 0x52), // 39 Aegean
            HexToColor32(0x32, 0x5B, 0x6A), // 40 Prussian
            HexToColor32(0x31, 0x4F, 0x79), // 41 Ultramarine
            HexToColor32(0x56, 0x48, 0x78), // 42 Indigo
            HexToColor32(0x56, 0x48, 0x78), // 43 Mauve
            HexToColor32(0xEB, 0x2E, 0x50), // 44 Alizarin
            HexToColor32(0x69, 0xA2, 0x18), // 45 Chartreuse
            HexToColor32(0x2F, 0x4D, 0x07), // 46 Peridot
            HexToColor32(0x69, 0xA2, 0x18), // 47 Mint
            HexToColor32(0x34, 0x56, 0x52), // 48 Cornflower
            HexToColor32(0x06, 0x57, 0x75), // 49 Sapphire
            HexToColor32(0x65, 0xA0, 0xF3), // 50 Periwinkle
            HexToColor32(0x31, 0x4F, 0x79), // 51 Lilac
            HexToColor32(0x50, 0x18, 0xDD), // 52 Violet
            HexToColor32(0x2F, 0x12, 0x78), // 53 Tanzanite
            HexToColor32(0x90, 0x63, 0x47), // 54 Leather
            HexToColor32(0x45, 0x28, 0x17), // 55 Chocolate
            HexToColor32(0x25, 0x10, 0x07), // 56 Espresso
            HexToColor32(0x5A, 0x3F, 0x31), // 57 Burnt Umber
            HexToColor32(0x99, 0x95, 0x93), // 58 Fog
            HexToColor32(0x62, 0x64, 0x66), // 59 Slate
            HexToColor32(0x49, 0x4A, 0x4D), // 60 Smoke
        };

        private static Color32 HexToColor32(byte r, byte g, byte b)
        {
            return new Color32(r, g, b, 255);
        }

        /// <summary>
        /// Decodes a Rec Room color int32 + optional Rgbcolor field into a Unity Color.
        /// </summary>
        internal static Color DecodeColor(SandboxColorableData colorData)
        {
            if (colorData == null)
                return Color.white;

            int raw = colorData.Color;
            int r = (raw >> 8)  & 0xFF;
            int g = (raw >> 16) & 0xFF;
            int b = (raw >> 24) & 0xFF;

            // If any upper byte is non-zero, it's a custom RGB color
            if (r != 0 || g != 0 || b != 0)
                return new Color(r / 255f, g / 255f, b / 255f);

            // Check the Rgbcolor protobuf field if present
            if (colorData.Rgbcolor != null &&
                (colorData.Rgbcolor.R != 0 || colorData.Rgbcolor.G != 0 || colorData.Rgbcolor.B != 0))
            {
                return new Color(colorData.Rgbcolor.R, colorData.Rgbcolor.G, colorData.Rgbcolor.B);
            }

            // Palette index lookup
            int index = raw & 0xFF;
            if (index >= 0 && index < Palette.Length)
                return Palette[index];

            return Color.white;
        }
    }
}
