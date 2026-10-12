namespace BetterNotepad.Markdown.Sample;

internal static class SampleText
{
    public const string Document = """
        ---
        title: Notatki projektowe
        tags: [markdown, podgląd]
        ---

        # BetterNotepad — podgląd Markdown

        Lekki, **natywny** podgląd bez WebView. Tekst po polsku: zażółć gęślą jaźń, *Łódź*, ***ważne***, ~~stare~~, `kod w linii` oraz [link do repozytorium](https://github.com/NemeWT10/BetterNotepad "Repozytorium"). Autolink: <https://example.com/docs> i www.example.org. Skrót klawiszowy: <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>P</kbd>. Przypis[^1], emoji :rocket: :tada: i H<sub>2</sub>O oraz x<sup>2</sup>.

        <!-- komentarz HTML, którego nie widać -->

        ## Akapity i nagłówki

        Drugi akapit jest dłuższy, żeby sprawdzić zawijanie wierszy, interlinię i ogólny „oddech” układu. Dobry podgląd nie męczy oczu: tekst ma stałą szerokość kolumny, wygodny kontrast i równe odstępy między blokami, a nagłówki wyraźnie dzielą treść na części.
        Ta linia jest kontynuacją tego samego akapitu (miękki podział wiersza).

        ### Trzeci poziom
        #### Czwarty poziom
        ##### Piąty poziom
        ###### Szósty poziom

        ---

        ## Cytaty i wywołania

        > Cytat jednopoziomowy z *kursywą* i `kodem`. Prosta myśl, dobrze oddzielona od reszty tekstu pionowym paskiem w kolorze akcentu.
        >
        > > Cytat zagnieżdżony — też czytelny.

        > [!NOTE]
        > Informacja pomocnicza. Zawiera **pogrubienie** i [link](https://example.com).

        > [!TIP]
        > Wskazówka: użyj <kbd>Ctrl</kbd>+<kbd>P</kbd>, aby szybko otworzyć plik.

        > [!IMPORTANT]
        > Ważne: ustawienia są zapisywane per użytkownik.

        > [!WARNING]
        > Ostrzeżenie: zmiana kodowania może zepsuć polskie znaki.
        >
        > Drugi akapit w tym samym wywołaniu.

        > [!CAUTION]
        > Uwaga: operacja jest nieodwracalna.

        ## Listy

        - Pierwszy punkt
        - Drugi punkt z długim opisem, który zawija się do kolejnej linii, żeby pokazać wcięcie wiszące i odstęp od znacznika
          - Zagnieżdżony punkt
          - Kolejny zagnieżdżony
            - Trzeci poziom
        - Trzeci punkt

        1. Zainstaluj program
        2. Otwórz plik `README.md`
        3. Przełącz widok na **Podgląd**
           1. Podpunkt numerowany
           2. Drugi podpunkt

        ### Zadania

        - [x] Natywny renderer Markdown
        - [x] Podświetlanie składni w blokach kodu
        - [ ] Eksport do PDF
        - [ ] Zsynchronizowane przewijanie
          - [x] Mapowanie linii na pozycję
          - [ ] Przewijanie dwukierunkowe

        ## Kod

        Blok kodu z podświetlaniem:

        ```csharp
        public sealed record Heading(int Level, string Text)
        {
            // Zwraca anchor w stylu GitHub
            public string Slug => Text.ToLowerInvariant().Replace(' ', '-');
        }

        foreach (var h in headings)
        {
            Console.WriteLine($"{h.Level}: {h.Text}");   // zażółć gęślą jaźń
        }
        ```

        ```json
        {
          "name": "BetterNotepad",
          "version": "0.1.0",
          "features": ["markdown", "pdf", "tabs"],
          "portable": true,
          "ratio": 1.55
        }
        ```

        ```python
        def fib(n: int) -> int:
            # klasyczna rekurencja
            return n if n < 2 else fib(n - 1) + fib(n - 2)

        print([fib(i) for i in range(10)])
        ```

        Blok bez języka, ze zbyt długą linią (przewija się poziomo):

        ```
        $ dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./artifacts/release
        Build succeeded.
        ```

        ## Tabele

        | Funkcja            | Status      |  Priorytet | Uwagi                              |
        | :----------------- | :---------: | ---------: | ---------------------------------- |
        | Podgląd Markdown   | gotowe      |          1 | Renderer natywny, bez WebView2     |
        | Mermaid / LaTeX    | w toku      |          2 | Leniwie przez WebView2             |
        | Eksport do PDF     | planowane   |          3 | Z `FlowDocument`                   |
        | Spis treści        | gotowe      |         10 | Lista nagłówków z numerami linii   |

        ## Obrazy

        Obraz względny z folderu pliku (ładowany asynchronicznie):

        ![Gradient testowy](assets/sample.png "Obraz testowy")

        Brakujący plik: ![Brak pliku](assets/nie-ma.png) oraz zdalny: ![Odznaka](https://img.shields.io/badge/licencja-MIT-blue).

        <p align="center">
          <img src="assets/icon.png" width="64" alt="Ikona">
        </p>

        <p align="center"><b>BetterNotepad</b> — notatnik dla osób technicznych</p>

        ## Diagramy i wzory

        Wzór w linii $E = mc^2$ oraz blok:

        $$
        \int_0^1 x^2\,dx = \frac{1}{3}
        $$

        ```mermaid
        graph LR
          A[Edytor] --> B[Podgląd]
          B --> C{Mermaid?}
        ```

        ## Odnośniki referencyjne

        Zobacz [dokumentację][docs] oraz [stronę projektu][home]. Przejdź do [akapitów](#akapity-i-nagłówki).

        [docs]: https://example.com/docs "Dokumentacja"
        [home]: https://github.com/NemeWT10/BetterNotepad

        [^1]: To jest treść przypisu dolnego, z `kodem` i [linkiem](https://example.com).
        """;
}
