namespace LingDongDao.Perception;

internal static class PythonRuntime
{
    public static string PythonPath => Environment.GetEnvironmentVariable("LINGDONGDAO_PYTHON") ??
        FindLocalFile(Path.Combine(".venv", "Scripts", "python.exe")) ?? "python";

    public static string ModelPath => Environment.GetEnvironmentVariable("LINGDONGDAO_GUI_MODEL") ??
        FindLocalFile(Path.Combine("models", "gpa", "model.onnx")) ??
        Path.Combine(AppContext.BaseDirectory, "models", "gpa", "model.onnx");

    private static string? FindLocalFile(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int level = 0; level < 6 && directory is not null; level++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
