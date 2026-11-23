// vInfo for ITcSmTreeItem.CreateChild of PLC objects (tc_plc_create): what the Automation Interface expects per subtype.
// Pure logic (no IDE), so the unit tests link this file.
//
//   602/604/608 program, function block, action    language (IECLANGUAGETYPES)
//   603/609     function, method                   { language, return type }
//   610         interface method                   { return type } (no implementation, so no language)
//   611/612     property, interface property       return type
//   613/614     Get/Set accessor                   language
//   623         DUT alias                          { declaration text } (TYPE X : <base type>; END_TYPE)
//   others      nothing
//
// Source: Beckhoff Automation Interface, "Creating PLC objects" (TREEITEMTYPE_PLCITFMETH [0] return data type,
// TREEITEMTYPE_PLCDUTALIAS [0 opt] declaration text). Because the samples there disagree with the table in places, for 610
// and 623 further candidates are tried in order: the plain string, then no vInfo (the declaration is written afterwards).
//
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace TcAgent;

public static class PlcCreateInfo
{
    /// <summary>The vInfo values to try for CreateChild, in this order (an entry may be null = no vInfo).
    /// declaration: for 623 the alias declaration (TYPE X : INT; END_TYPE).</summary>
    public static List<object> Candidates(int subType, int language, string returnType, string declaration) => subType switch
    {
        602 or 604 or 608 or 613 or 614 => new List<object> { language },
        603 or 609 => new List<object> { new object[] { language, returnType ?? "BOOL" } },
        610 => new List<object> { new object[] { returnType ?? "BOOL" }, returnType ?? "BOOL", null },
        611 or 612 => new List<object> { returnType ?? "BOOL" },
        623 => string.IsNullOrWhiteSpace(declaration) ? new List<object> { null } : new List<object> { new object[] { declaration }, declaration, null },
        _ => new List<object> { null }
    };

    static readonly Regex AliasDecl = new(@"^\s*TYPE\s+\w+\s*:\s*(?<t>[^;]+?)\s*;", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>The base type of an alias declaration "TYPE T_X : INT;" (null = not an alias declaration, e.g. a STRUCT).</summary>
    public static string AliasBaseType(string declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration)) return null;
        var text = Regex.Replace(declaration, @"\(\*.*?\*\)|//[^\n]*", "", RegexOptions.Singleline);
        var m = AliasDecl.Match(text);
        if (!m.Success) return null;
        var t = m.Groups["t"].Value.Trim();
        return Regex.IsMatch(t, @"^(STRUCT|UNION)\b|^\(", RegexOptions.IgnoreCase) ? null : t;
    }

    /// <summary>The declaration of an alias when none is given.</summary>
    public static string AliasDeclaration(string name, string baseType) => $"TYPE {name} : {baseType};\nEND_TYPE\n";

    /// <summary>The declaration of an interface method when none is given (no implementation part).</summary>
    public static string InterfaceMethodDeclaration(string name, string returnType) =>
        $"METHOD {name}{(string.IsNullOrWhiteSpace(returnType) ? "" : " : " + returnType.Trim())}\nVAR_INPUT\nEND_VAR\n";
}
