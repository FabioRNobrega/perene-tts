namespace PereneTts.Services.Epub;

public interface INumberToWordsConverter
{
    bool TryConvert(int number, string? language, out string words);
}
