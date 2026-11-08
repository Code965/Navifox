namespace Navifox.Core.DataGen;

/// <summary>Comune: nome, sigla della provincia, regione, CAP e codice catastale (per il codice fiscale).</summary>
public sealed record ItalianPlace(string City, string Province, string Region, string PostalCode, string CadastralCode);

/// <summary>Dati di esempio italiani per il generatore (nomi, cognomi, comuni, vie…). Tutti inventati o di uso comune.</summary>
public static class ItalianData
{
    public static readonly string[] MaleNames =
    [
        "Marco", "Luca", "Giuseppe", "Francesco", "Alessandro", "Andrea", "Matteo", "Lorenzo", "Davide", "Simone",
        "Federico", "Riccardo", "Stefano", "Paolo", "Giovanni", "Antonio", "Roberto", "Fabio", "Daniele", "Michele",
        "Gabriele", "Tommaso", "Leonardo", "Edoardo", "Filippo", "Nicola", "Emanuele", "Salvatore", "Vincenzo", "Pietro",
        "Mario", "Claudio", "Massimo", "Alberto", "Enrico", "Giorgio", "Carlo", "Sergio", "Diego", "Mattia",
        "Christian", "Samuele", "Alessio", "Domenico", "Cristian", "Bruno", "Franco", "Giacomo", "Elia", "Raffaele"
    ];

    public static readonly string[] FemaleNames =
    [
        "Giulia", "Francesca", "Sara", "Martina", "Chiara", "Alessia", "Valentina", "Federica", "Elena", "Silvia",
        "Laura", "Anna", "Maria", "Paola", "Giorgia", "Sofia", "Aurora", "Alice", "Ginevra", "Beatrice",
        "Elisa", "Roberta", "Monica", "Cristina", "Simona", "Serena", "Ilaria", "Claudia", "Barbara", "Daniela",
        "Camilla", "Emma", "Greta", "Noemi", "Arianna", "Veronica", "Erika", "Michela", "Rebecca", "Irene",
        "Marta", "Lucia", "Teresa", "Angela", "Rita", "Caterina", "Matilde", "Vittoria", "Bianca", "Nicole"
    ];

    public static readonly string[] Surnames =
    [
        "Rossi", "Russo", "Ferrari", "Esposito", "Bianchi", "Romano", "Colombo", "Ricci", "Marino", "Greco",
        "Bruno", "Gallo", "Conti", "De Luca", "Mancini", "Costa", "Giordano", "Rizzo", "Lombardi", "Moretti",
        "Barbieri", "Fontana", "Santoro", "Mariani", "Rinaldi", "Caruso", "Ferrara", "Galli", "Martini", "Leone",
        "Longo", "Gentile", "Martinelli", "Vitale", "Lombardo", "Serra", "Coppola", "De Santis", "D'Angelo", "Marchetti",
        "Parisi", "Villa", "Conte", "Ferraro", "Ferri", "Fabbri", "Bianco", "Marini", "Grasso", "Valentini",
        "Messina", "Sala", "De Angelis", "Gatti", "Pellegrini", "Palumbo", "Sanna", "Farina", "Rizzi", "Monti",
        "Cattaneo", "Morelli", "Amato", "Silvestri", "Mazza", "Testa", "Grassi", "Pellegrino", "Carbone", "Giuliani",
        "Benedetti", "Barone", "Rossetti", "Caputo", "Montanari", "Guerra", "Palmieri", "Bernardi", "Martino", "Fiore",
        "De Rosa", "Ferretti", "Bellini", "Basile", "Riva", "Donati", "Piras", "Vitali", "Battaglia", "Sartori",
        "Neri", "Costantini", "Milani", "Pagano", "Ruggiero", "Sorrentino", "D'Amico", "Orlando", "Damico", "Negri"
    ];

    public static readonly ItalianPlace[] Places =
    [
        new("Roma", "RM", "Lazio", "00185", "H501"), new("Milano", "MI", "Lombardia", "20121", "F205"),
        new("Napoli", "NA", "Campania", "80133", "F839"), new("Torino", "TO", "Piemonte", "10121", "L219"),
        new("Palermo", "PA", "Sicilia", "90133", "G273"), new("Genova", "GE", "Liguria", "16121", "D969"),
        new("Bologna", "BO", "Emilia-Romagna", "40121", "A944"), new("Firenze", "FI", "Toscana", "50122", "D612"),
        new("Bari", "BA", "Puglia", "70121", "A662"), new("Catania", "CT", "Sicilia", "95121", "C351"),
        new("Venezia", "VE", "Veneto", "30121", "L736"), new("Verona", "VR", "Veneto", "37121", "L781"),
        new("Messina", "ME", "Sicilia", "98122", "F158"), new("Padova", "PD", "Veneto", "35122", "G224"),
        new("Trieste", "TS", "Friuli-Venezia Giulia", "34121", "L424"), new("Brescia", "BS", "Lombardia", "25121", "B157"),
        new("Parma", "PR", "Emilia-Romagna", "43121", "G337"), new("Taranto", "TA", "Puglia", "74121", "L049"),
        new("Prato", "PO", "Toscana", "59100", "G999"), new("Modena", "MO", "Emilia-Romagna", "41121", "F257"),
        new("Reggio Emilia", "RE", "Emilia-Romagna", "42121", "H223"), new("Perugia", "PG", "Umbria", "06121", "G478"),
        new("Ravenna", "RA", "Emilia-Romagna", "48121", "H199"), new("Livorno", "LI", "Toscana", "57123", "E625"),
        new("Cagliari", "CA", "Sardegna", "09124", "B354"), new("Foggia", "FG", "Puglia", "71121", "D643"),
        new("Rimini", "RN", "Emilia-Romagna", "47921", "H294"), new("Salerno", "SA", "Campania", "84121", "H703"),
        new("Ferrara", "FE", "Emilia-Romagna", "44121", "D548"), new("Sassari", "SS", "Sardegna", "07100", "I452"),
        new("Latina", "LT", "Lazio", "04100", "E472"), new("Monza", "MB", "Lombardia", "20900", "F704"),
        new("Pescara", "PE", "Abruzzo", "65121", "G482"), new("Bergamo", "BG", "Lombardia", "24121", "A794"),
        new("Vicenza", "VI", "Veneto", "36100", "L840"), new("Trento", "TN", "Trentino-Alto Adige", "38122", "L378"),
        new("Bolzano", "BZ", "Trentino-Alto Adige", "39100", "A952"), new("Ancona", "AN", "Marche", "60121", "A271"),
        new("Lecce", "LE", "Puglia", "73100", "E506"), new("Udine", "UD", "Friuli-Venezia Giulia", "33100", "L483"),
        new("Como", "CO", "Lombardia", "22100", "C933"), new("Pisa", "PI", "Toscana", "56125", "G702"),
        new("Lucca", "LU", "Toscana", "55100", "E715"), new("Aosta", "AO", "Valle d'Aosta", "11100", "A326"),
        new("Potenza", "PZ", "Basilicata", "85100", "G942"), new("Campobasso", "CB", "Molise", "86100", "B519"),
        new("L'Aquila", "AQ", "Abruzzo", "67100", "A345"), new("Catanzaro", "CZ", "Calabria", "88100", "C352"),
        new("Siena", "SI", "Toscana", "53100", "I726"), new("Mantova", "MN", "Lombardia", "46100", "E897")
    ];

    public static readonly string[] Streets =
    [
        "Via Roma", "Via Garibaldi", "Via Mazzini", "Via Cavour", "Corso Italia", "Via Dante Alighieri", "Via Verdi",
        "Via Marconi", "Piazza della Repubblica", "Via XX Settembre", "Viale dei Mille", "Via Matteotti", "Corso Vittorio Emanuele II",
        "Via Leopardi", "Via Manzoni", "Via Carducci", "Via Fiume", "Via Trieste", "Via Milano", "Via Torino", "Via Napoli",
        "Via della Libertà", "Viale Europa", "Via dei Tigli", "Via delle Rose", "Via San Francesco", "Via Gramsci",
        "Piazza Garibaldi", "Via Colombo", "Via Petrarca", "Via Pascoli", "Via Kennedy", "Via Martiri della Libertà", "Via IV Novembre"
    ];

    public static readonly string[] CompanyWords =
    [
        "Tecno", "Italia", "Servizi", "Logistica", "Costruzioni", "Informatica", "Trasporti", "Alimentari", "Consulting",
        "Impianti", "Edilizia", "Sistemi", "Commerciale", "Meccanica", "Elettronica", "Arredamenti", "Distribuzione", "Energia"
    ];

    public static readonly string[] CompanySuffixes = ["S.r.l.", "S.p.A.", "S.n.c.", "S.a.s.", "S.r.l.s."];

    /// <summary>Domini riservati alla documentazione (RFC 2606): nessuna email generata può arrivare a una persona reale.</summary>
    public static readonly string[] EmailDomains = ["example.com", "example.org", "example.net", "example.test"];

    public static readonly string[] Words =
    [
        "casa", "lavoro", "progetto", "cliente", "ordine", "prodotto", "servizio", "consegna", "fattura", "contratto",
        "richiesta", "nota", "verifica", "documento", "pratica", "gestione", "ufficio", "magazzino", "articolo", "spedizione",
        "pagamento", "rinnovo", "offerta", "preventivo", "assistenza", "controllo", "aggiornamento", "riunione", "attività", "report",
        "nuovo", "urgente", "completato", "in", "attesa", "di", "per", "con", "il", "la", "del", "della", "mensile", "annuale", "semplice"
    ];

    public static readonly string[] Countries = ["Italia"];
}
