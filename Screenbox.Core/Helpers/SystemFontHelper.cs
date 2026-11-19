using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace Screenbox.Core.Helpers;

public static unsafe class SystemFontHelper
{
    private static readonly string[] FallbackFonts =
    [
        "Arial",
        "Calibri",
        "Consolas",
        "Georgia",
        "Microsoft JhengHei UI",
        "Microsoft YaHei UI",
        "Segoe UI",
        "Tahoma",
        "Times New Roman",
        "Trebuchet MS",
        "Verdana",
        "Yu Gothic UI"
    ];

    public static IReadOnlyList<string> GetInstalledFontFamilies()
    {
        try
        {
            Guid factoryGuid = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"); // IDWriteFactory
            nint factory = 0;
            int hr = DWriteCreateFactory(0 /* DWRITE_FACTORY_TYPE_SHARED */, &factoryGuid, &factory);
            if (hr < 0 || factory == 0)
                return FallbackFonts;

            try
            {
                // IDWriteFactory::GetSystemFontCollection is vtable slot 3
                nint* factoryVtbl = *(nint**)factory;
                var getSystemFontCollection = (delegate* unmanaged[Stdcall]<nint, nint*, int, int>)factoryVtbl[3];
                nint collection = 0;
                hr = getSystemFontCollection(factory, &collection, 0);
                if (hr < 0 || collection == 0)
                    return FallbackFonts;

                try
                {
                    nint* collectionVtbl = *(nint**)collection;
                    // IDWriteFontCollection::GetFontFamilyCount = slot 3, GetFontFamily = slot 4
                    var getFontFamilyCount = (delegate* unmanaged[Stdcall]<nint, uint>)collectionVtbl[3];
                    var getFontFamily = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)collectionVtbl[4];

                    uint count = getFontFamilyCount(collection);
                    var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string currentLocale = CultureInfo.CurrentUICulture.Name;

                    for (uint i = 0; i < count; i++)
                    {
                        nint family = 0;
                        if (getFontFamily(collection, i, &family) < 0 || family == 0)
                            continue;

                        try
                        {
                            // IDWriteFontFamily::GetFamilyNames is vtable slot 6
                            nint* familyVtbl = *(nint**)family;
                            var getFamilyNames = (delegate* unmanaged[Stdcall]<nint, nint*, int>)familyVtbl[6];
                            nint names = 0;
                            if (getFamilyNames(family, &names) < 0 || names == 0)
                                continue;

                            try
                            {
                                string? name = ExtractLocalizedName(names, currentLocale)
                                               ?? ExtractLocalizedName(names, "en-us")
                                               ?? ExtractNameAtIndex(names, 0);

                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    fonts.Add(name);
                                }
                            }
                            finally
                            {
                                Marshal.Release(names);
                            }
                        }
                        finally
                        {
                            Marshal.Release(family);
                        }
                    }

                    return fonts.Count > 0
                        ? fonts.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList()
                        : FallbackFonts;
                }
                finally
                {
                    Marshal.Release(collection);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch
        {
            return FallbackFonts;
        }
    }

    private static string? ExtractLocalizedName(nint names, string locale)
    {
        nint* namesVtbl = *(nint**)names;
        // IDWriteLocalizedStrings::FindLocaleName is vtable slot 4
        var findLocaleName = (delegate* unmanaged[Stdcall]<nint, char*, uint*, int*, int>)namesVtbl[4];

        uint index = 0;
        int exists = 0;
        fixed (char* localePtr = locale)
        {
            int hr = findLocaleName(names, localePtr, &index, &exists);
            if (hr >= 0 && exists != 0)
            {
                return ExtractNameAtIndex(names, index);
            }
        }

        return null;
    }

    private static string? ExtractNameAtIndex(nint names, uint index)
    {
        nint* namesVtbl = *(nint**)names;
        // GetStringLength = slot 7, GetString = slot 8
        var getStringLength = (delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)namesVtbl[7];
        var getString = (delegate* unmanaged[Stdcall]<nint, uint, char*, uint, int>)namesVtbl[8];

        uint length = 0;
        if (getStringLength(names, index, &length) < 0 || length == 0)
            return null;

        char[] buffer = new char[length + 1];
        fixed (char* bufferPtr = buffer)
        {
            if (getString(names, index, bufferPtr, length + 1) < 0)
                return null;
        }

        return new string(buffer, 0, (int)length);
    }

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int factoryType, Guid* iid, nint* factory);
}
