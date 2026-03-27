namespace ClaudeManager.Hub.Services;

/// <summary>GPU inventory entry returned by nvidia-smi discovery.</summary>
/// <param name="Index">Zero-based GPU index (as reported by nvidia-smi).</param>
/// <param name="Name">GPU model name, e.g. "NVIDIA A100-SXM4-80GB".</param>
/// <param name="MemoryTotalMb">Total VRAM in MiB.</param>
/// <param name="MemoryFreeMb">Free VRAM in MiB at discovery time.</param>
public record GpuInfo(int Index, string Name, int MemoryTotalMb, int MemoryFreeMb);
