using System.Collections.Generic;
using System.Linq;

namespace Blade.IR;

/// <summary>
/// Represents the concrete physical placement of every required image.
/// This stage follows image planning and assigns hub addresses to each image so later
/// layout solving and memory-map rendering reason about real occupied hub ranges.
/// </summary>
public sealed class ImagePlacement
{
    public ImagePlacement(IReadOnlyList<ImagePlacementEntry> images, ImagePlacementEntry entryImage)
    {
        Requires.NotNull(images);
        Requires.NotNull(entryImage);
        Requires.That(images.Contains(entryImage));

        Images = images;
        EntryImage = entryImage;
    }

    /// <summary>
    /// Gets every concretely placed image in hub-memory order.
    /// </summary>
    public IReadOnlyList<ImagePlacementEntry> Images { get; }

    /// <summary>
    /// Gets the placed entry image at the start of the output file.
    /// </summary>
    public ImagePlacementEntry EntryImage { get; }

    /// <summary>
    /// Gets the exclusive end address of the reserved image arena in hub bytes.
    /// </summary>
    public HubAddress ImageArenaEndAddressExclusive => Images.Count == 0
        ? new HubAddress(0)
        : Images[^1].HubEndAddressExclusive;
}

/// <summary>
/// Represents the physical hub-memory reservation for one image.
/// </summary>
public sealed class ImagePlacementEntry(ImageDescriptor image, HubAddress hubStartAddressBytes, int sizeBytes)
{
    /// <summary>
    /// Gets the logical image whose hub placement is being described.
    /// </summary>
    public ImageDescriptor Image { get; } = Requires.NotNull(image);

    /// <summary>
    /// Gets the image start address in hub bytes.
    /// </summary>
    public HubAddress HubStartAddressBytes { get; } = hubStartAddressBytes;

    /// <summary>
    /// Gets the reserved hub size in bytes for this image.
    /// </summary>
    public int SizeBytes { get; } = Requires.Positive(sizeBytes);

    /// <summary>
    /// Gets the exclusive end address of the image reservation in hub bytes.
    /// </summary>
    public HubAddress HubEndAddressExclusive => HubStartAddressBytes + SizeBytes;
}

/// <summary>
/// Assigns concrete hub-memory reservations to the required images.
/// </summary>
public static class ImagePlacer
{
    /// <summary>
    /// Gets the provisional reserved size in hub bytes for every image in the current implementation.
    /// </summary>
    public const int ReservedImageSizeBytes = 0x800;

    /// <summary>
    /// Places the entry image at hub address zero and packs all other images after it.
    /// </summary>
    public static ImagePlacement Place(ImagePlan imagePlan)
    {
        Requires.NotNull(imagePlan);

        List<ImagePlacementEntry> placements = [];
        HubAddress nextHubAddress = new(0);

        ImagePlacementEntry entryPlacement = CreatePlacement(imagePlan.EntryImage, nextHubAddress);
        placements.Add(entryPlacement);
        nextHubAddress = entryPlacement.HubEndAddressExclusive;

        foreach (ImageDescriptor image in imagePlan.Images)
        {
            if (ReferenceEquals(image, imagePlan.EntryImage))
                continue;

            ImagePlacementEntry placement = CreatePlacement(image, nextHubAddress);
            placements.Add(placement);
            nextHubAddress = placement.HubEndAddressExclusive;
        }

        return new ImagePlacement(placements, entryPlacement);
    }

    private static ImagePlacementEntry CreatePlacement(ImageDescriptor image, HubAddress hubStartAddressBytes)
    {
        Requires.NotNull(image);
        return new ImagePlacementEntry(image, hubStartAddressBytes, ReservedImageSizeBytes);
    }
}
