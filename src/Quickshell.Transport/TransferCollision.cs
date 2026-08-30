namespace Quickshell.Transport;

/// <summary>What to do about a symbolic link met while walking a directory.</summary>
public enum LinkPolicy
{
    /// <summary>
    /// Copy the link itself, pointing where it pointed. The default, because following one is how a
    /// recursive copy walks into a loop or drags in an entire filesystem.
    /// </summary>
    Copy,

    /// <summary>Copy what it points at, as though the link were the file. Chosen deliberately.</summary>
    Follow,

    /// <summary>Leave it out.</summary>
    Skip,
}

/// <summary>
/// The four answers to a name that is already taken. There is no fifth.
///
/// <para>Four is a decision and not a shortage. A tool that offers more is a tool whose dialog a
/// user stops reading, and a user who stops reading picks whichever option ends the questions —
/// which is a data-loss mechanism wearing the costume of a preference.</para>
/// </summary>
public enum CollisionAnswer
{
    /// <summary>Replace what is there.</summary>
    Overwrite,

    /// <summary>Leave what is there and move on.</summary>
    Skip,

    /// <summary>Write beside it under a name that is free.</summary>
    Rename,

    /// <summary>Replace it only if what is being copied is newer.</summary>
    TakeNewer,
}

/// <summary>
/// Both sides of a name that is already taken, so the question can be answered rather than guessed.
/// </summary>
/// <param name="Path">The destination that already exists.</param>
/// <param name="Length">How big the thing being copied is.</param>
/// <param name="Modified">When it last changed.</param>
/// <param name="ExistingLength">How big what is already there is.</param>
/// <param name="ExistingModified">When that last changed.</param>
public readonly record struct Collision(string Path, long Length, DateTimeOffset Modified,
                                        long ExistingLength, DateTimeOffset ExistingModified)
{
    /// <summary>Whether what is being copied is newer than what is there.</summary>
    public bool IsNewer => Modified > ExistingModified;
}

/// <summary>
/// One answer, and whether it stands for the rest.
///
/// <para>"Apply to the rest" is not a convenience. Asked four hundred times, a person picks the
/// option that ends the asking, so the way to keep the answers meaningful is to ask once.</para>
/// </summary>
/// <param name="Answer">What to do.</param>
/// <param name="ForTheRest">Whether every later collision gets the same answer without asking.</param>
public readonly record struct CollisionChoice(CollisionAnswer Answer, bool ForTheRest = false);

/// <summary>Asked when a destination name is taken.</summary>
/// <param name="what">Both sides of it.</param>
/// <param name="cancellationToken">Abandons the transfer.</param>
/// <returns>What to do, and whether it stands for the rest.</returns>
public delegate ValueTask<CollisionChoice> CollisionCheck(Collision what,
                                                          CancellationToken cancellationToken);
