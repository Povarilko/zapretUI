using System;
using System.Text;

namespace ZapretUi
{
    // Вывод теста (test zapret.ps1) пишется в OEM-кодировке консоли (в русской Windows — CP866).
    // Вместо пакета кодировок — встроенная таблица CP866: ноль зависимостей.
    public static class Oem
    {
        // CP866: байты 0x80..0xFF -> юникод
        static readonly char[] High = Build();

        static char[] Build()
        {
            var map = new char[128];
            // 0x80-0x9F: А-Я
            for (int i = 0; i < 32; i++) map[i] = (char)('А' + i);
            // 0xA0-0xAF: а-п
            for (int i = 0; i < 16; i++) map[32 + i] = (char)('а' + i);
            // 0xB0-0xBF: ░▒▓│┤╡╢╖╕╣║╗╝╜╛┐
            char[] b = { '░', '▒', '▓', '│', '┤', '╡', '╢', '╖', '╕', '╣', '║', '╗', '╝', '╜', '╛', '┐' };
            Array.Copy(b, 0, map, 48, 16);
            // 0xC0-0xCF: └┴┬├─┼╞╟╚╔╩╦╠═╬╧
            char[] c = { '└', '┴', '┬', '├', '─', '┼', '╞', '╟', '╚', '╔', '╩', '╦', '╠', '═', '╬', '╧' };
            Array.Copy(c, 0, map, 64, 16);
            // 0xD0-0xDF: ╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀
            char[] d = { '╨', '╤', '╥', '╙', '╘', '╒', '╓', '╫', '╪', '┘', '┌', '█', '▄', '▌', '▐', '▀' };
            Array.Copy(d, 0, map, 80, 16);
            // 0xE0-0xEF: р-я
            for (int i = 0; i < 16; i++) map[96 + i] = (char)('р' + i);
            // 0xF0-0xF1: Ёё
            map[112] = 'Ё'; map[113] = 'ё';
            // 0xF2-0xFF: ╒╓╔╕╖╗╘╙╚╛╜╝╞╟
            char[] f = { '╒', '╓', '╔', '╕', '╖', '╗', '╘', '╙', '╚', '╛', '╜', '╝', '╞', '╟' };
            Array.Copy(f, 0, map, 114, 14);
            return map;
        }

        public static string Decode(byte[] bytes, int index, int count)
        {
            var sb = new StringBuilder(count);
            for (int i = index; i < index + count; i++)
            {
                byte b = bytes[i];
                sb.Append(b < 0x80 ? (char)b : High[b - 0x80]);
            }
            return sb.ToString();
        }

        public static string Decode(byte[] bytes) => Decode(bytes, 0, bytes.Length);
    }
}
