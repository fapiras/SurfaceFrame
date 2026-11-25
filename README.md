# Surface Frame

Cornice fotografica Windows x64 per Surface Pro 6. La presentazione legge le immagini da OneDrive personale. Un tocco apre la dashboard Home Assistant in WebView2. Il menu **Comandi**, in basso a destra, permette di aprire il desktop, cambiare modalità o gestire la presentazione.

## Primo avvio sul Surface

1. Chiudi Surface Frame se è aperto. Estrai tutto lo ZIP e apri **Avvia-Surface-Frame.cmd** dalla cartella estratta.
2. Attendi l’apertura delle impostazioni. Il launcher controlla WebView2 e lo installa se manca. Nel pacchetto con i sorgenti usa il kit .NET 10 già presente o lo scarica temporaneamente, verifica SHA-512 e compila Surface Frame. La prima preparazione richiede accesso ai server Microsoft e NuGet.
3. Per gli avvii successivi cerca **Surface Frame** nel menu Start. Il launcher installa la versione in **%LOCALAPPDATA%/SurfaceFrame/app/** e rimuove i suoi file temporanei. In caso di errore indica la causa e il percorso di **installazione.log**.

Configura OneDrive e Home Assistant nelle impostazioni. In **Sistema** puoi scegliere l’avvio automatico. Il workflow Windows x64 prepara anche un pacchetto precompilato che usa lo stesso launcher. Per una build manuale è disponibile **build.ps1**.

## Aggiornamenti dall’app

Scarica **SurfaceFrame-Setup-win-x64.zip** dalla [pagina Releases](https://github.com/fapiras/SurfaceFrame/releases), estrailo e avvia **Avvia-Surface-Frame.cmd** una volta. Dalla versione 1.2.0 il verificatore è incluso nel pacchetto.

In **Impostazioni → Sistema → Aggiornamenti** usa **Controlla aggiornamenti** e, quando disponibile, **Aggiorna e riavvia**. L’app verifica l’attestazione Sigstore del pacchetto, l’identità del repository e del workflow e la versione dell’eseguibile prima di avviarlo. Se la nuova versione non conferma l’avvio, riparte quella precedente.

Il workflow pubblica la release quando cambia la versione sul ramo `main`, dopo aver verificato i due ZIP e respinto una copia alterata. Le attestazioni vengono generate da GitHub Actions tramite OIDC. Non sono richiesti segreti di firma o accessi a GitHub sul Surface.

## OneDrive personale

1. In **Impostazioni → OneDrive**, premi **Collega OneDrive**. Nel browser seleziona l’account Microsoft personale che contiene le foto e conferma la richiesta di rclone. Surface Frame include rclone e richiede accesso in sola lettura (`Files.Read` e `offline_access` per mantenere la sessione).
2. In **Cartelle OneDrive → Sfoglia OneDrive**, apri una cartella e premi **Aggiungi questa cartella**. Puoi anche digitare un percorso come **/Foto/Cornice**. Scegli se includere le sottocartelle e premi **Salva e aggiorna**.
3. Surface Frame carica una foto alla volta in memoria e verifica che sia orizzontale. Con rclone legge gli originali fino a 48 MiB e applica il limite di decodifica di 60 MP. La compatibilità HEIC/HEIF dipende dal formato dell’originale e dal decoder. **Disconnetti OneDrive** rimuove la configurazione rclone di Surface Frame dal profilo Windows.

[Rclone e OneDrive](https://rclone.org/onedrive/) · [Licenza rclone](rclone-LICENSE.txt)

### Configurazione avanzata Microsoft Graph

Per i link di condivisione e per la configurazione esistente con un ID applicazione Microsoft, apri **Configurazione avanzata Microsoft Graph**.

La registrazione dell’applicazione in Microsoft Entra richiede un tenant in cui puoi registrare app. L’account che possiede o può accedere alle foto è l’account Microsoft personale che selezioni quando accedi da Surface Frame.

1. Usa **Apri Microsoft Entra** nelle impostazioni e vai in **App registrations → New registration**. Usa il nome **Surface Frame** e scegli il tipo di account che include sia le directory organizzative sia gli account Microsoft personali. Surface Frame usa l’autorità di accesso **consumers**.
2. In **Authentication → Add a platform → Mobile and desktop applications** aggiungi l’URI di reindirizzamento http://localhost. La configurazione usa un client pubblico e l’accesso interattivo nel browser di sistema. Lascia vuota la configurazione dei segreti applicativi.
3. In **API permissions → Add a permission → Microsoft Graph → Delegated permissions** aggiungi **Files.Read** per i percorsi delle tue cartelle. Se scegli di usare link di condivisione, aggiungi **Files.ReadWrite**. È il permesso minimo documentato da Graph per l’endpoint /shares con un account personale. Il codice utilizza richieste GET per leggere il contenuto. Il riscatto del link può associare l’accesso alla cartella all’account connesso.
4. Copia **Application (client) ID** in **Impostazioni → OneDrive**. Aggiungi un percorso come **/Foto/Cornice** oppure incolla un link di visualizzazione OneDrive personale **1drv.ms** o **onedrive.live.com**. Per usare i link, seleziona **Consenti l’accesso ai link condivisi**.
5. Scegli quali cartelle attivare e se includere le sottocartelle. Premi **Salva e accedi a Microsoft**, seleziona il tuo account personale e leggi la richiesta di consenso di Microsoft. Al termine il catalogo si aggiorna. **Salva e aggiorna** richiede una nuova lettura del catalogo.

L’app impiega i metadati Graph e le dimensioni delle anteprime per stimare l’orientamento. Il contatore distingue le immagini compatibili da quelle da verificare. Le foto selezionate vengono filtrate durante la decodifica, dopo la lettura dell’orientamento EXIF dell’originale o con l’orientamento già applicato da OneDrive all’anteprima. Per i file HEIC/HEIF viene tentata un’anteprima o una conversione in JPEG da parte di Graph. La disponibilità per singola foto dipende da OneDrive.

La modalità predefinita riempie il display con ritaglio centrato. Puoi includere foto quadrate e verticali da **Presentazione**. In **Aspetto** scegli se ritagliare le immagini a schermo intero oppure vederle per intero e se mostrare nome e data. La data usa i dati disponibili dal servizio, che può fornire la data di modifica. Durante la presentazione l’app tiene in memoria le immagini necessarie per il passaggio e la navigazione tra le foto. La cache offline, disattivabile in **Presentazione**, conserva sul PC fino a 48 copie JPEG ridotte entro 96 MiB. Le richieste HTTPS leggono i dati direttamente in RAM. Per limitare i picchi, l’anteprima usa fino a 20 MiB, l’originale fino a 48 MiB e la decodifica scarta immagini oltre 60 MP se non è utilizzabile un’anteprima più piccola.

## Home Assistant e desktop

1. In **Impostazioni → Home Assistant** inserisci l’URL completo della dashboard, copiato dalla barra del browser quando la plancia dedicata è aperta. Usa l’indirizzo finale, incluso il percorso, lo schema e l’eventuale porta.
2. Salva e tocca la cornice. Esegui una prima volta l’accesso a Home Assistant nella finestra integrata. Il profilo WebView2 conserva la sessione.
3. I tocchi, i clic, lo scorrimento e la tastiera nella dashboard rinnovano il timer. Dopo 120 secondi di inattività la cornice torna in primo piano. **Cornice** rientra subito nella presentazione.
4. Seleziona **Comandi → Desktop Windows** per accedere al desktop. Per rientrare nella presentazione, fai doppio clic sull’icona Surface Frame nell’area di notifica oppure seleziona **Torna alla cornice** dal suo menu. Il timer opera solo nella modalità Home Assistant.

**Comandi** contiene le azioni per cambiare modalità, aprire le impostazioni, minimizzare e chiudere. Nella cornice trovi anche **Foto precedente**, **Pausa presentazione** e **Foto successiva**. Home Assistant usa tutta la finestra e il browser integrato parte quando apri la dashboard.

In **Aspetto → Modalità notte** puoi programmare una fascia in cui attenuare le foto o mostrare uno schermo nero. Un tocco sullo schermo nero ripristina la cornice e il termine della fascia conclude la modalità notte. Lo schermo nero mantiene Windows e Home Assistant in esecuzione.

## Manutenzione

- **Configurazione** — %LOCALAPPDATA%\SurfaceFrame\settings.json. Il file settings.example.json contiene valori iniziali e può essere usato come esempio. Tratta i link di condivisione come dati riservati quando copi quella configurazione.
- **Sessioni** — La cartella auth contiene la cache MSAL protetta per l’utente Windows. La cartella home-assistant contiene il profilo WebView2, con la sua sessione e la normale cache web della dashboard. Usa **Disconnetti Microsoft** per rimuovere la sessione Microsoft dell’app.
- **Diagnostica** — **Sistema → Apri il log**. Il registro riporta eventi, tipologie e codici di errore, ruota a circa 512 KiB e conserva una generazione precedente.
- **Aggiornamento** — Chiudi Surface Frame dal menu di notifica e sostituisci l’intera cartella dell’applicazione. Se cambi percorso, disattiva e riattiva l’avvio automatico in Impostazioni.
- **Rimozione** — Disattiva l’avvio automatico, chiudi Surface Frame e rimuovi la cartella dell’app. Il profilo locale rimane disponibile per una reinstallazione. Rimuovilo separatamente se vuoi cancellare configurazione e sessioni.

## Verifiche

| Verifica | Esito |
| --- | --- |
| Core compilato con Roslyn su Windows | Superato con requisiti e impostazioni, 40 cicli casuali, aggiunta e rimozione di foto |
| Graph simulato con risposte HTTP in memoria | Superato per percorsi e link, paginazione, ricorsione, redirect e assenza del token OAuth sul CDN |
| Markup WPF e risorse | Caricati i modelli di entrambe le finestre con il parser WPF. Verificati i nomi dei gestori degli eventi |
| Autenticazione MSAL | Compilato il modulo con gli assembly MSAL 4.73.1 disponibili localmente. Accesso reale e build con le dipendenze di progetto da verificare |
| Launcher a un clic | Verificati struttura dello ZIP e riferimenti dei file. Esecuzione su Windows ancora da verificare |
| Build completa ed eseguibile x64 | Compilato e pubblicato il runtime autonomo 1.0.2 per Windows x64 con .NET 10.0.404 |
| Firma e orientamento EXIF in 1.0.2 | Verificati manifesto valido, firma o hash alterati, versione errata e immagini JPG/PNG/WebP con e senza rotazione. Per le prove su Linux è stata usata una controparte di test per il solo oggetto immagine WPF |
| Aggiornamento e ripristino sul Surface | Da collaudare su Windows dopo la configurazione del canale GitHub e la pubblicazione di una versione successiva |
| EXIF, touch, installazione e prova prolungata | La suite include fixture JPG, PNG e WebP. La sua esecuzione, il test su OneDrive reale e la prova end-to-end sul Surface sono ancora da eseguire |

Per il collaudo finale, usa una cartella con almeno due orizzontali, una verticale, una quadrata e una ruotata via EXIF. Verifica la rotazione e poi esegui il percorso **foto → dashboard → desktop → area di notifica → cornice**. Controlla che l’uso della plancia rinnovi i 120 secondi e che dal desktop il ritorno sia manuale. Prova una temporanea disconnessione di rete e una sessione di alcune ore. Controlla anche un intervallo della modalità notte a cavallo di mezzanotte.

## Riferimenti

- [Graph: accesso agli elementi condivisi](https://learn.microsoft.com/graph/api/shares-get)
- [Graph: elenco della cartella](https://learn.microsoft.com/graph/api/driveitem-list-children)
- [Graph: dimensioni e orientamento](https://learn.microsoft.com/graph/api/resources/photo)
- [Graph: anteprime, originali e conversione](https://learn.microsoft.com/graph/api/driveitem-list-thumbnails) · [Download](https://learn.microsoft.com/graph/api/driveitem-get-content) · [Conversione](https://learn.microsoft.com/graph/api/driveitem-get-content-format)
- [Microsoft Entra: registrazione applicazione](https://learn.microsoft.com/entra/identity-platform/quickstart-register-app) · [URI desktop](https://learn.microsoft.com/entra/msal/dotnet/how-to/default-reply-uri)
- [MSAL: conservazione dei token](https://learn.microsoft.com/entra/msal/dotnet/how-to/token-cache-serialization)
- [WebView2 in WPF](https://learn.microsoft.com/microsoft-edge/webview2/platforms/wpf)
