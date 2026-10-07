using System.Runtime.InteropServices;
using System.Windows.Input;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS171's falsification, read on the layouts this desk has: a chord in the reference fires on the
/// key that types its character, wherever the layout put that key.
///
/// <para><b>Against Windows' own layouts and not a table of them.</b> For every layout installed here,
/// Windows is asked which key types each chord's character unshifted, and the gesture is asked what
/// that key types. A layout where the character needs AltGr or Shift has no such key and is said to
/// have no chord, which is what the keys reference tells its reader. No layout is loaded: the ones
/// already on the desk are the ones a person here types on.</para>
/// </summary>
public sealed class CharacterGestureTests
{
    private static readonly char[] Bound = ['\\', '-', ','];

    [Fact]
    public void EveryChordCharacterIsFoundWhereEachInstalledLayoutPutsIt()
    {
        nint[] layouts = Installed();
        int checkedPairs = 0;

        Assert.NotEmpty(layouts);

        foreach (nint layout in layouts)
        {
            foreach (char character in Bound)
            {
                short scanned = VkKeyScanExW(character, layout);

                // -1 is "this layout cannot type it"; a shift state above zero is "only with
                // something held", and neither is a key a Ctrl+Shift chord can use.
                if (scanned == -1 || (scanned >> 8) != 0)
                {
                    continue;
                }

                Key key = KeyInterop.KeyFromVirtualKey(scanned & 0xFF);

                Assert.Equal(character, CharacterGesture.Typed(key, layout));

                checkedPairs++;
            }
        }

        Assert.True(checkedPairs > 0, "no installed layout types any of the chord characters unshifted");
    }

    /// <summary>No chord is left on an Oem position, which is the asymmetry the line was filed for.</summary>
    [Fact]
    public void NoChordIsBoundToAnOemPosition()
    {
        string[] onOem = OnSta(() => new MainWindow().InputBindings
                                                    .OfType<KeyBinding>()
                                                    .Where(binding => binding.Key.ToString().StartsWith("Oem", StringComparison.Ordinal))
                                                    .Select(binding => binding.Key.ToString())
                                                    .ToArray());

        Assert.Empty(onOem);
    }

    private static nint[] Installed()
    {
        int count = GetKeyboardLayoutList(0, null);
        nint[] layouts = new nint[count];

        int filled = GetKeyboardLayoutList(count, layouts);

        return layouts[..filled];
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread sta = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception caught)
            {
                failed = caught;
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        if (failed is not null)
        {
            throw new InvalidOperationException("the STA work failed", failed);
        }

        return result;
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, nint[]? layouts);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanExW(char character, nint layout);
}
