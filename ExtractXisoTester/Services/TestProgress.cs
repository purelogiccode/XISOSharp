namespace ExtractXisoTester.Services;

public record TestProgress(
    string CurrentFile,
    int FileIndex,
    int TotalFiles,
    string CurrentTest,
    string StatusText,
    bool IsComplete = false
);
