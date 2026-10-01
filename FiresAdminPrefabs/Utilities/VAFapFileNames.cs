using System;
using System.IO;
using System.Text;

namespace VerdantsAscent.Utilities
{
    public static class VAFapFileNames
    {
        private const string EmptyNameReplacement = "_unnamed";
        private const char InvalidCharacterReplacement = '_';

        private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

        // Turns a piece, blueprint or tab name into a file or folder name that keeps as close to the
        // visible name as Windows allows.
        public static string ToSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return EmptyNameReplacement;
            var safeName = new StringBuilder(name.Length);
            foreach (char character in name)
                safeName.Append(Array.IndexOf(InvalidFileNameCharacters, character) >= 0 ? InvalidCharacterReplacement : character);
            string trimmed = safeName.ToString().Trim();
            return trimmed.Length == 0 ? EmptyNameReplacement : trimmed;
        }
    }
}
