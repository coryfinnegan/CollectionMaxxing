namespace CollectionMaxxing.Domain.Libraries;

/// <summary>What a member may do in a library. Each role includes everything the lower ones can do.</summary>
public enum LibraryRole
{
    Viewer = 1,
    Editor = 2,
    Owner = 3,
}
