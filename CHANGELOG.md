# Changelog de Zentra

Zentra es un fork de [optimizerDuck](https://github.com/itsfatduck/optimizerDuck) (GPL v3). Aquí solo se anotan los cambios de Zentra; los del proyecto original están en sus releases.

## [Sin publicar]

### Mejoras, Fase 3B: informe antes/después, huella e inventario (19/11/2026, PR 14, sobre el PR 13)

- **Antes/después (F3-03):** el informe ya tenía puntaje, arranque, RAM, espacio libre y disco, con sus diferencias. Lo nuevo:
  - **Disco:** DiskSpd varía entre una prueba y otra. Las diferencias de menos del 10 % se muestran, pero no se marcan como mejora ni empeoramiento, y el informe lo explica.
  - **Preset:** el informe nombra el preset aplicado.
  - **CLI:** el PDF del CLI ahora lleva el informe escrito, siempre con la plantilla. El CLI nunca llama a la IA: nada del cliente sale del PC sin el técnico presente.
- **Huella del informe (F3-04):**
  - `ZENTRA-SHA256.txt` junto al PDF, con la huella del PDF y de `session.json` (formato de sha256sum).
  - El PDF imprime la huella del `session.json` del que sale.
  - Sesión y el CLI muestran la huella del PDF para entregarla aparte.
  - `Zentra.exe --verify <carpeta>` comprueba la carpeta (código 7 si algo cambió o falta). Nunca abre rutas fuera de ella.
- **Inventario (F3-06):** "Exportar inventario" en Sesión, o `--inventory` en el CLI.
  - `inventario.csv`: programas con versión, editor, fecha, tamaño y alcance, más la edición de Windows y si está activado. Lleva BOM y el separador del idioma, para Excel. Las celdas que empiezan con `=`, `+`, `-` o `@` se neutralizan, para que no corran como fórmulas.
  - `winget-export.json`, para `winget import` en el PC nuevo.
  - Solo lee: no lee claves de producto y no se envía a la IA. Los programas de usuario son los del cliente, aunque el técnico haya elevado con otra cuenta.
  - "Programas más pesados" del informe ahora también lee la cuenta del cliente.
- **Tests:** 1584 en total, 0 fallos.
- **Sin probar en el PC real:** el inventario, la huella en pantalla y `--verify`.

### Mejoras, Fase 3A: presets y línea de comandos (19/11/2026, PR 13, sobre el PR 12)

Primer bloque de la Fase 3 (estandarizar el servicio). La guía está en `docs/PRESETS-Y-CLI.md`.

- **Presets en JSON (F3-01):** cada preset tiene nombre, versión, los ajustes por GUID, apps exactas y fragmentos de nombre. El Servicio estándar es el preset incluido.
  - En Sesión: "Importar preset", "Exportar preset" y "Volver al Servicio estándar". La confirmación nombra el preset.
  - Al importar, Zentra valida el archivo y nombra cada problema:
    - campos desconocidos o GUID que no existen;
    - ajustes o apps repetidos;
    - ajustes Experto sin `allowRisky`;
    - apps protegidas;
    - fragmentos de menos de 6 letras;
    - archivos de más de 256 KB.
  - Las apps protegidas nunca se quitan.
- **Línea de comandos (F3-02):** por ejemplo, `Zentra.exe --preset estandar --diagnose --report <carpeta> --client <nombre> [--silent] [--keep-apps] [--allow-risky]`.
  - **Mismo orden que la página Sesión:** valida, previsualiza, confirma (salvo con `--silent`), crea el punto de restauración, mide antes, aplica, mide después y escribe el informe.
  - **Sin punto de restauración no aplica nada.**
  - **Ajustes Experto:** necesitan `allowRisky` en el preset y además `--allow-risky`.
  - **Códigos de salida fijos:** 0, 1–6 y 10.
  - **Salida:** va a la consola que lo abrió, o al archivo o tubería si está redirigida. También queda en `zentra-cli.txt`.
  - La sesión guarda el nombre del preset aplicado.
- **Tests:** 1558 en total, 0 fallos. Cubren la ida y vuelta del preset, cada rechazo, el orden de los pasos del CLI, la cancelación, el punto de restauración fallido y el resultado parcial.
- **Sin probar en el PC real:** el CLI y los botones de presets en pantalla.

### Mejoras, Fase 2D: pruebas y datos (19/11/2026, PR 12, sobre el PR 11)

Cierra la Fase 2 del plan de mejoras.

- **Aplicar y revertir de verdad en el CI (F2-01):** `ProfileApplyRevertCiTests` corre solo en el runner de GitHub, que es desechable y elevado. Hace cuatro cosas con cada ajuste del Servicio estándar:
  - lo previsualiza;
  - lo aplica;
  - comprueba que quedó como pide el perfil;
  - lo revierte, y exige que el registro, los servicios, las tareas y las directivas vuelvan al valor de antes.
  
  Pasó en el CI. En el PC de desarrollo se omite.
- **Banco de pruebas (F2-02):** `docs/BANCO-DE-PRUEBAS.md` documenta:
  - cinco niveles: tests, CI, Windows Sandbox, máquina virtual y PCs reales;
  - la matriz de PCs que faltan;
  - un checklist por PR;
  - la regla de que Zentra lo abre Diego, por la virtualización MSIX.
  
  Además, `tools/publicar-prueba.ps1` publica una versión de prueba y `tools/zentra-prueba.wsb` la abre en Windows Sandbox, sin red y en solo lectura.
- **Tabla de TBW (F2-10):** 27 modelos con su resistencia oficial (Kingston A400 y NV2, Samsung 870 EVO, Crucial MX500, BX500 y P3 Plus). Cada valor cita la hoja de datos del fabricante.
  - Quedan fuera a propósito el Crucial P3 (sin tabla oficial clara) y el P3 Plus de 4 TB (las fuentes no coinciden).
  - El BX500 de 480 GB es de 120 TBW, no 160.
- **Desgaste SATA con smartctl (F2-09):** Zentra trae `smartctl.exe` de smartmontools 7.5 (GPL-2.0), sin modificar.
  - **Procedencia:** se verificó la firma OpenPGP del instalador oficial. El instalador no se ejecutó: el exe se extrajo de sus datos y su SHA-256 coincide con la lista firmada.
  - **Seguridad:** Zentra comprueba el hash antes de cada uso, y lo ejecuta con el archivo bloqueado contra escritura. `smartctl -j -x` solo lee.
  - **Qué lee:** el total escrito por el host, en este orden:
    - NVMe: Data Units Written.
    - ATA: Device Statistics.
    - Atributos cuyo nombre en la base de smartctl dice la unidad.
    
    Nunca las escrituras NAND.
  - **Resultado:** R-SSD-WEAR compara ese total con la tabla de TBW. Un SATA de la tabla deja de ser "no medible"; uno que no está en la tabla sigue siéndolo. Sin elevación, queda "acceso denegado".
  - **Pendiente legal (Fase 1):** toda release que incluya `smartctl.exe` debe llevar también su código fuente (GPL-2.0, sección 3). Está en `Tools\smartctl.SOURCE.txt`.
- **Sin probar en el PC real:** la lectura de smartctl del Kingston A400 de Diego. Necesita Zentra elevado y abierto por Diego.
- **Sin cambios:** la tabla de BIOS sigue con una placa.

### Mejoras, Fase 2C: aplicar con más cuidado (19/11/2026, PR 11, sobre el PR 10)

- **Vista previa del perfil (F2-03):** antes de aplicar el Servicio estándar, el diálogo muestra cada valor que cambiaría en el PC (ahora → después), agrupado por ajuste. Es un ensayo:
  - Las 18 primitivas que cambian el sistema (registro, servicios, tareas, directiva local, plan de energía y comandos) solo describen el cambio; no escriben ni registran nada.
  - Un test exige que los ajustes del perfil cambien el PC solo por esas primitivas.
  - **Verificado en el PC de Diego:** 52 valores en 17 ajustes; tras cancelar, los valores siguen sin existir y el servicio de IA sigue en Automático.
- **Cuenta del cliente (F2-05):** si Zentra corre con otra cuenta de administrador, todo lo de usuario va a la cuenta de quien está en el PC (`HKEY_USERS\<SID>`):
  - lo que pasa por `RegistryService`: optimizaciones, perfil, Personalizar y reversiones;
  - OneDrive en el diagnóstico;
  - Bloatware (`-User <SID>` validado);
  - el Gestor de Inicio (Run, RunOnce, carpeta Inicio y tareas UWP).
  
  Sesión lo avisa.
- **Abrir sin elevar (F2-06, auditoría Z-30):** enlaces, PDF, carpetas, archivos y Configuración se abren con el Explorador del escritorio (`IShellDispatch2.ShellExecute`), como el usuario de la sesión. Si falla, se usa `explorer.exe`. El Administrador de tareas, el Programador de tareas y el Panel de control siguen elevados a propósito. **Verificado:** el navegador abierto desde Zentra corre sin elevar.
- **"Abrir carpeta" en Sesión:** sin sesión abre la carpeta de sesiones anteriores o dice que se crea con "Medir antes"; antes no hacía nada.
- **Hallazgo de pruebas:** Zentra abierto por Claude hereda la virtualización de `AppData` del paquete MSIX de Claude, y sus datos quedan en la carpeta del paquete. Eso explicaba el "Ubicación no disponible" anotado desde la Fase 1. Para probar, Zentra lo abre Diego.
- **Sin probar en pantalla:** "Abrir carpeta" con Zentra abierto por Diego (Diego decidió seguir).

### Mejoras, Fase 2B: diagnósticos nuevos de solo lectura (19/11/2026, PR 10, sobre el PR 9)

- **Reglas nuevas:**
  - R-SECURE-PLATFORM: arranque seguro apagado (Media) o sin TPM 2.0 (Baja).
  - R-UEFI-CA2023: sin el certificado "Windows UEFI CA 2023" (Baja). Solo informa; no se toca el firmware.
  - R-WU-HEALTH: servicio de Windows Update deshabilitado (Alta), actualización atascada o 45 días sin actualizar Windows (Media). Solo cuentan las actualizaciones con KB, no las apps de la Store ni las firmas de Defender.
  - R-RESTORE-OFF: protección del sistema apagada (Media).
- **Batería:** suma los ciclos de carga del informe de `powercfg`, que también es el respaldo de la capacidad. El número de serie no sale de la sonda.
- **Puntos de restauración semanales (auditoría Z-08):** la acción por fin aparece en Almacenamiento y registra cuándo enciende la protección.
- **Arranque (F2-08):** ya existía (evento 100 y causantes 101–110).
- **Catálogo:** 3 pasos manuales nuevos (44 acciones) y 4 reglas nuevas (33 reglas).
- **Verificado en el PC de Diego:** las sondas leen datos reales sin elevación (TPM 2.0, certificado aplicado, última KB hace 8 días, protección activa).

### Mejoras, Fase 2A: protecciones y diagnóstico honesto (19/11/2026, PR 9, sobre el PR 8)

Primer bloque del plan de mejoras posterior a la auditoría. La Fase 1 del plan (bloqueantes de seguridad y legales) queda de última por decisión de Diego; hasta cerrarla no se atiende a clientes.

- **Protecciones (F2-04):**
  - Fotos protegida con su nombre real.
  - En Bloatware, "Segura" es solo lo que quita el perfil; el resto pasa a "Precaución".
  - Las tareas de Windows Update, Defender, restauración, BitLocker, TPM y Edge Update no se pueden deshabilitar, borrar ni sobrescribir, y Zentra dice por qué.
- **Reglas recalibradas (F2-07):**
  - R-THERMAL cuenta incidentes, no eventos, y sin temperatura medida queda en Media.
  - R-RAM-XMP no se evalúa en portátiles con SMBIOS; en escritorio, SMBIOS sola es Media.
  - R-UNSTABLE sin pantallazos azules es Media.
  - R-SSD-HEALTH es Crítica solo con el disco "no sano"; errores acumulados solos, Alta.
  - R-APPS-OUTDATED: si winget falla, es "no medible".
- **Sin datos inventados:**
  - sfc lee su resultado real en CBS.log.
  - Una sonda que falla ya no tumba el diagnóstico.
  - La velocidad configurada de la RAM ya no se rellena con la del módulo.
- **Puntaje:** el de referencia del PC de Diego pasa de 51 a 70.

### Fase 7: opciones con consentimiento escrito (19/11/2026, PR 7, fusionada sin prueba en el PC)

Se apoyó en la rama de la Fase 6B (PR 6).

- **Ventana de consentimiento:** cada opción pide marcar "El cliente aceptó por escrito este cambio". El texto dice qué cambia, el riesgo, si hace falta reiniciar y cómo se deshace. Sin ventana (tests, sin interfaz) la respuesta es siempre "no".
- **Pausar Windows Update 7 días** (ACT-WU-PAUSE7, Moderate): los valores nativos de pausa que escribe Configuración, nunca más de 7 días. Se reanuda sola; revertir la reanuda ya.
- **Excluir bibliotecas de juegos de Defender** (ACT-DEFENDER-EXCL, Moderate): solo carpetas de Steam y Epic. Nunca una unidad, Windows, Program Files, ProgramData, el perfil, el Escritorio, Documentos, AppData, Temp ni Descargas. Revertir quita cada exclusión.
- **Modo juego avanzado** (ACT-GAMEMODE-ADV, Risky): apaga la Integridad de memoria (HVCI) y la Plataforma de máquina virtual. Pide prueba A/B con el mismo juego y reiniciar. Mientras está aplicado, la página Sesión muestra una tarjeta con el botón "Volver a activar la protección".
- Ninguna de las tres está en el perfil "Servicio estándar".

**Revisión de seguridad de Fable** (bloqueó el merge hasta corregir dos hallazgos altos):
- Inyección de PowerShell con comillas tipográficas (‘ ’ ‚ ‛) en la ruta de la exclusión: `PowerShellText` escapa las cinco comillas; también se aplicó a Bloatware y a la tarjeta de red.
- Filtro de carpetas demasiado permisivo (las rutas vienen de HKCU y de manifiestos de Epic que cualquier usuario escribe): ahora exige ruta local ya normalizada, disco fijo, sin junctions ni symlinks, fuera de todos los perfiles y con marcas reales de biblioteca (manifiestos de Steam, `.egstore` de Epic). En Program Files solo `steamapps\common` o `Epic Games\<juego>`.
- Además: la pausa no se puede encadenar (7 días de espera tras cada pausa), el consentimiento queda en el log, no se aplica nada si no se pueden leer las exclusiones actuales, y el modo juego avanzado no aplica si la Integridad de memoria está bloqueada por UEFI o por directiva.
- Fable re-verificó los arreglos: nada bloquea el merge.

**Estado:** PR 7 fusionado el 19/11/2026 sin la prueba en el PC. Diego apagó Smart App Control en su PC ese día, así que la prueba ya se puede hacer.

### Fase 6B: diagnóstico ampliado y mantenimiento (19/11/2026, PR 6, fusionada sin prueba en el PC)

**Diagnósticos nuevos**
- R-MON-HZ (Alta): monitor por debajo de sus Hz. Solo monitores físicos con EDID, sin clonar, en su resolución nativa y con 10 Hz o más de diferencia. Monitores con la configuración de pantalla de Windows (CCD), no con WMI (que da una fila por adaptador).
- R-MON-PORT (Crítica): sobremesa con gráfica dedicada sin monitores y un monitor externo en la integrada. Gráficas clasificadas con los bits del kernel (D3DKMT); sin Vortice.
- R-GPU-HYBRID (Alta): portátil donde Windows sí cambia de gráfica y juegos vistos sin preferencia. Al snapshot solo van conteos, nunca rutas.
- R-APPS-OUTDATED (Media): 5 o más programas con versión nueva en winget.
- R-PUA-OFF (Media): bloqueo de aplicaciones no deseadas apagado **o en auditoría** (desviación: el plan solo nombra el valor 0; la auditoría no bloquea nada).
- R-BATTERY (Alta), R-THERMAL (Alta), R-NET-100M (Media), R-RAM-IDLE-HIGH (Alta, desde la medición de la Sesión), R-BACKUP-NONE (Media).

**Acciones nuevas**
- Monitores a sus Hz máximos: prueba, aplica sin guardar, verifica y pide confirmación a quien está frente al PC en 15 s; sin respuesta vuelve solo. Diseñado con la consulta Fable (por RustDesk el técnico no ve una pantalla negra).
- Juegos en la gráfica de alto rendimiento, bloqueo de PUA (al revertir queda en auditoría, nunca apagado), navegadores sin segundo plano, tarjeta de red sin ahorro de energía (con -NoRestart), puntos de restauración semanales, carpetas grandes fuera del indexador (ISearchCrawlScopeManager).
- Escaneo completo y sin conexión de Defender (Reparar Windows), "Actualizar todos" (Programas), programas que más ocupan (PDF local).
- Temperatura: LibreHardwareMonitorLib 0.9.6 (MPL-2.0) con el driver PawnIO, que Zentra instala con winget solo para medir, con consentimiento, y desinstala enseguida.

**Estado:** PR 6 fusionado el 19/11/2026 sin la prueba en el PC. Con Smart App Control ya apagado en el PC de Diego, la prueba se puede hacer.

### Fase 6: perfil del servicio, privacidad e instalador (19/11/2026)

**Perfil "Servicio estándar"** (tarjeta en la página Sesión, entre "Medir antes" y "Medir después")
- 22 ajustes Safe y reversibles: IA (Click To Do, servicio de IA, Edge, Paint, Bloc de notas), apps preinstaladas y de dispositivos, TCP, anuncios y sugerencias, Noticias e intereses, Meet Now, historial de actividad, ubicación, Buscar mi dispositivo, entrega P2P, Sensor de almacenamiento, TRIM, menús más rápidos, búsqueda web y Recomendado del Inicio, anuncios de Microsoft 365.
- Quita el bloatware de la sección 6.2 (incluye GetHelp, Family y Power Automate) y juegos de terceros preinstalados. No toca apps de streaming o redes sociales: el cliente puede usarlas.
- Fuera, a propósito: la limpieza DISM (irreversible), GPU y energía (dependen del hardware), lo Moderate y Risky, y el inicio rápido.
- Antes de cambiar nada pide confirmación (dice cuántos ajustes y qué apps) y crea el punto de restauración; si Windows ya hizo uno en las últimas 24 h, ese lo cubre. Sin punto de restauración no se aplica.
- Usa el mismo camino que la página Optimizar: cada ajuste guarda su reversión y va al historial y al PDF. "Revertir perfil" devuelve cada ajuste a su valor; las apps quitadas se reinstalan desde la Microsoft Store.

**Ajustes nuevos tomados de Win11Debloat** (MIT, aviso en THIRD-PARTY-NOTICES)
- Servicio de IA de Windows (WSAIFabricSvc) en Manual, IA de Edge, Paint y Bloc de notas, y no descargar apps de dispositivos. Valores exactos de sus `Regfiles`.
- "Anuncios de Microsoft 365 en Configuración" ya existía. El "historial de búsqueda local" ya no está en Win11Debloat y no se agregó.

**Programas** (página nueva, winget)
- Catálogo cerrado: 7-Zip, VLC, LibreOffice, Google Chrome e Intel Driver & Support Assistant (solo en PCs Intel). IDs verificados con `winget show`; comando exacto del plan.
- Instalar y Desinstalar pasan por el diálogo de progreso y el historial. Los drivers de AMD y NVIDIA no están en winget: hay botones a sus páginas oficiales.

**Guía de BIOS** (página nueva)
- ASRock B450M-HDV R4.0 con lo que documenta su manual oficial (2021): entrar (F2/Supr), DRAM Frequency, guardar (F10) y valores de fábrica (F9).
- El manual no documenta XMP ni la memoria de video (UMA): esos pasos lo dicen en vez de inventar una ruta.

**Correcciones al motor heredado** (encontradas al probar en el PC de Diego)
- En Windows 11 25H2 un administrador no puede escribir valores en `HKLM\SOFTWARE`, pero sí crear claves debajo. optimizerDuck pedía escritura completa en cada clave intermedia y fallaba al crear cualquier clave nueva en HKLM (8 de los ajustes del perfil). Ahora pide solo leer y crear en el camino.
- Noticias e intereses: el driver de protección de Windows (UCPD) bloquea escribir su directiva directamente, aun como administrador. Ahora se pone por directiva de grupo local, como gpedit ("Permitir widgets" = Deshabilitada), y se revierte igual. Solo en Windows 11 Pro o superior: Home no tiene esa directiva.
- Revertir un valor cuya clave ya no existe cuenta como hecho; antes el archivo de reversión quedaba atascado y el ajuste figuraba aplicado para siempre.

**Prueba en el PC de Diego** (19/11/2026, autorizada)
- Primer intento: 9 cambiados y 8 fallos (lo de HKLM). Tras la corrección: 16 cambiados y 1 fallo (UCPD). Con la directiva de grupo: **17 cambiados, 4 ya estaban bien, 1 no aplica, 0 fallos**.
- Revertir: 17 revertidos, 0 fallos. Los 16 valores del registro que se fotografiaron antes quedaron idénticos, y los archivos de directiva local y las claves vacías se borraron.
- Se quitaron 7 apps en el primer intento (Clipchamp, Noticias, El Tiempo, Obtener ayuda, Solitaire, Power Automate y To Do). No vuelven con "Revertir perfil".
- winget: 7-Zip se instaló y se desinstaló (código 0 las dos veces).

### Fase 5: informe con IA desde la app (19/11/2026)

**Qué hace**
- Tarjeta **Informe con IA** en la página Sesión: se pega la clave de la API de Claude, se pulsa "Redactar informe" y se ve el texto antes de generar el PDF.
- El PDF empieza con **Resumen** y **Qué hacer, en orden** (cada acción dice quién la hace: Zentra, el técnico, la BIOS o una pieza nueva), las advertencias y una nota de quién redactó el texto.
- Sin clave, sin internet o con cualquier error de la API, el texto lo escribe la plantilla de Zentra con los mismos datos. Todo PDF lleva texto escrito.

**La clave**
- En el PC del cliente vive solo en memoria y se borra al cerrar Zentra. Se lee de un cuadro de contraseña sin enlace de datos, el cuadro se limpia al usarla y el portapapeles se vacía al pegarla.
- "Recordar en este equipo" (cifrada con DPAPI para el usuario de Windows) solo aparece si Zentra se abre con `--equipo-tecnico`. Si se abre sin ese argumento, la copia recordada se borra.
- Nunca se escribe en logs, `session.json`, el PDF ni la configuración: los logs y los crash logs tapan cualquier `sk-ant-…` por si acaso. Si la API responde que la clave no sirve, se borra de la memoria.
- Rechaza claves de administrador y claves con espacios o saltos de línea.

**Lo que se envía** (sección 8.1 del plan, con lista blanca de campos)
- Equipo (modelos y tamaños), puntaje antes y después, mediciones con su diferencia, hallazgos con su evidencia y sus acciones, lo que no se pudo medir y lo que se aplicó. Son los mismos textos en español del PDF.
- Nunca: el nombre del cliente, fechas, el id de la sesión, los programas que retrasan el arranque ni números de disco. Lo enviado queda guardado en `session.json` junto al texto.

**La llamada**
- `POST /v1/messages` con `HttpClient`, modelo `claude-sonnet-5-5`, versión `2026-10-21`, 60 s, sin proxy.
- El formato de la sección 8.2 se fuerza con *structured outputs* (`output_config.format` con esquema JSON). `action_id` es una lista cerrada con solo las acciones de los hallazgos de ese PC.
- Máximo 2 llamadas por informe: si la revisión rechaza la primera respuesta, se reintenta una vez con los motivos; si vuelve a fallar, plantilla.

**La revisión automática del texto**
- Cada acción debe estar en el catálogo y en los hallazgos; prioridades 1, 2, 3… sin repetir.
- Toda cifra del texto debe estar en los datos enviados (lee "6.100" y "41,0" en español e inglés). Las cifras pegadas a letras (DDR4, B450M) son nombres.
- Rechaza enlaces, correos, lo de la lista negra (activadores, limpiadores de registro, desactivar Defender o Windows Update…), programas que el servicio no recomienda y emojis o flechas (la fuente del PDF no los tiene).

**Cambios respecto al plan**
- Se usan *structured outputs* en vez de `tool_choice` forzado: con Sonnet 5.5 un `tool_choice` forzado da error 400.
- Sin `temperature` (en Sonnet 5.5 da error 400) y versión de API `2026-10-21` en vez de `2023-06-01`.
- `max_tokens` 8000 en vez de 2000, para que la respuesta no llegue cortada.
- La tarjeta va en la página Sesión, no en una `ReportPage` aparte.
- La app pasa a `net10.0-windows10.0.17763.0` para poder borrar el historial del portapapeles (Win+V) al pegar la clave. Pesa 23 MB más.

**Revisión de seguridad (consultor Fable)**
- Sin hallazgos críticos ni altos. Se corrigieron los cuatro medios y dos bajos:
  - Al pegar la clave también se borra el historial del portapapeles.
  - Una respuesta con `null` cae a la plantilla en vez de lanzar excepción.
  - La lista negra busca un verbo de apagar o quitar junto a algo protegido, sin tildes ni mayúsculas.
  - Sin redirecciones automáticas, y un acceso denegado a la clave guardada no rompe la página.
- Los demás bajos quedan anotados en el plan para la Fase 6.

**Prueba real en el PC de Diego** (19/11/2026, con una clave exclusiva y límite de gasto)
- La IA redactó el informe en la primera llamada: 3.295 tokens de entrada y 1.015 de salida, unos USD 0,017.
- El texto pasó la revisión automática: 6 prioridades del catálogo y 5 advertencias, todo en español sencillo.
- La clave no aparece en ninguno de los archivos de `%LocalAppData%\Zentra` (log, sesiones, PDF, configuración) y no se creó la carpeta `Secrets` (modo cliente).

**Tests**
- Centinela: con una clave real en forma, ocho escenarios (respuesta buena, rechazada, 401, 500, 429, basura y excepciones que llevan la petición completa) y la clave no aparece en el log, el crash log, `session.json` ni el PDF. Control positivo en UTF-8, UTF-16 y Base64.
- El payload solo lleva campos de la lista blanca; solo cuatro clases tocan la clave.
- Validador (con las frases que pasaron la primera versión), cliente HTTP con respuestas simuladas y nulls, y servicio con reintento y plantilla.

### Fase 4: reparación y almacenamiento (19/11/2026)

**Reparar Windows** (página nueva)
- Corre, en este orden: DISM `/ScanHealth`, DISM `/RestoreHealth` (solo si la imagen tiene daños; si está sana se salta), `sfc /scannow` y `chkdsk C: /scan`.
- Pasa por `OperationRunner`: diálogo de progreso con el porcentaje de cada paso, reintento si algo falla y registro en el historial. Las reparaciones quedan como irreversibles.
- La salida se lee con la codificación de cada herramienta (UTF-16 de sfc, OEM 850 de DISM y chkdsk, UTF-8), así que no salen caracteres rotos.
- El estado de la imagen se lee con `Repair-WindowsImage -CheckHealth`, que no depende del idioma. `chkdsk` se interpreta por su código de salida (0 sin errores, 1 o 2 reparó, 3 necesita reparar al reiniciar).
- Cada paso tiene un límite de 60 minutos.

**Categoría Almacenamiento** (en Optimizar)
- Limpiar el almacén de componentes (ACT-DISM-CLEANUP, Safe): `/AnalyzeComponentStore` y `/StartComponentCleanup`. Irreversible.
- Activar el Sensor de almacenamiento (ACT-STORAGESENSE, Safe). Reversible.
- Activar TRIM (ACT-TRIM, Safe): `DisableDeleteNotification = 0`. Reversible.
- Comprimir Windows (ACT-COMPACTOS, Moderate): solo en discos de sistema de 256 GB o menos. Se revierte con `compact /CompactOS:never`. Si ya estaba comprimido, se salta.
- Borrar respaldos de actualizaciones (ACT-DISM-RESETBASE, Risky): irreversible, nunca en un perfil por defecto.
- DISM y Compact OS usan un límite de 60 minutos; el del resto de la app es de 2.

**Red**
- Ajuste automático de TCP en normal (ACT-TCP-NORMAL, Safe, en Rendimiento): lee el nivel actual con `Get-NetTCPSetting` (no depende del idioma) y al revertir pone el nivel anterior.

**Diagnóstico**
- Regla R-WIN-CORRUPT: archivos de Windows dañados según DISM CheckHealth (Alta, ACT-REPAIR-WIN).

**Prueba en el PC de Diego** (19/11/2026, solo la prueba autorizada)
- Storage Sense, TRIM y TCP ya estaban bien: quedaron como "No había nada que hacer" y sin datos de reversión.
- Reversión real: con Storage Sense apagado a propósito, se aplicó (0 a 1, cambio con reversión guardada) y se revirtió (1 a 0, archivo de reversión borrado, historial con "Revert"). Al final se dejó activo, como estaba.
- Sin probar en el PC: la limpieza con DISM, Reparar Windows, Compact OS y ResetBase. Están cubiertos por tests.

### Fase 3: medición antes/después y PDF (19/11/2026)

**Qué hace**
- Página **Sesión** (debajo de Diagnóstico) con tres pasos: medir antes, medir después y generar el PDF para el cliente. El nombre del cliente es opcional, se guarda solo en el PC del técnico y nunca va a la IA.
- Cada medición corre el diagnóstico completo y mide:
  - **Arranque:** mediana de los últimos 3. Usa el evento 100 de Diagnostics-Performance y, si Windows no lo registra, el plan B: del arranque del kernel (evento 12) al inicio de sesión (Winlogon 7001). También lee los culpables de los eventos 101 a 110 cuando existen.
  - **RAM en reposo:** promedio de 60 s, tomado al menos 5 minutos después de que arrancó el escritorio.
  - Espacio libre en C:, apps de inicio activas, procesos y servicios activos.
  - **Disco con DiskSpd:** lectura secuencial y aleatoria de 15 s cada una, sin caché y solo lectura. Crea un archivo de prueba de 1 GB y lo borra.
  - Si el inicio rápido está activo, para comparar siempre en el mismo modo.
- Las sesiones se guardan en `%LocalAppData%\Zentra\Sessions\<fecha>_<id>\` (`session.json` y el PDF).
- **PDF (QuestPDF, fuente Lato incluida):**
  - Puntaje antes y después, y tabla de métricas con el cambio en verde o rojo.
  - Lo que encontramos, lo que hicimos (desde el historial de optimizaciones), lo pendiente para el cliente (BIOS, hardware y manual), lo que hace lento el arranque, lo que no se pudo medir y notas.
  - Si falla, no deja un PDF a medias.
- **Regla nueva R-BOOT-SLOW:** arranque de más de 60 s (Media), con la acción ACT-STARTUP-REVIEW.
- **Apagados inesperados:** el pendiente dice "Revisar la estabilidad del perfil XMP/EXPO", no "Activar XMP", porque el plan lo pide como revisión.

**DiskSpd**
- El binario oficial de Microsoft tiene un EULA que no permite redistribuirlo. Zentra lo compila desde el código MIT (commit 5e7025b, DISKSPD 2.3) y lo incluye en `Tools\diskspd.exe` (ver `Resources/Tools/DiskSpd/BUILD.txt`).

**Arreglos**
- 65 textos en español tenían las tildes corruptas ("DiagnÃ³stico") por los scripts de traducción de las fases 1 y 2. Están reparados y hay un test que lo evita.

**Resultado en el PC de Diego** (19/11/2026)
- Arranque 5,6 s (del kernel al inicio de sesión: el evento 100 no se registra en esta build), RAM en reposo 11,0 GB (69,9 %, con apps de trabajo abiertas), 2 apps de inicio, 243 procesos, 113 servicios.
- Crucial P3: lectura secuencial 3.517 MB/s y aleatoria 4K 178.720 IOPS.
- PDF generado: puntaje 51 con los 4 hallazgos de la Fase 2.

### Fase 2: diagnóstico (19/11/2026)

**Qué hace**
- Página **Diagnóstico** (debajo del Panel). Solo lee: no cambia nada del PC. El escaneo arranca cuando el técnico pulsa el botón, después de que el cliente lo autoriza (aviso de la Ley 1581 en la página).
- 17 reglas de la sección 7 del plan, puntaje de 0 a 100 (sección 7.1), hallazgos por severidad con su evidencia y las acciones del catálogo, y una sección "No medible" con la razón de cada dato que no se pudo leer. Lo no medible nunca resta puntos ni se inventa.
- Catálogo cerrado de las 41 acciones de la sección 6.1. Un hallazgo con una acción fuera del catálogo se descarta.
- Tablas embebidas en el `.exe`: `ram_kits.json` (tu kit y los patrones de número de parte de Kingston, G.Skill, Corsair, Crucial Ballistix y XPG), `ssd_tbw.json` (Crucial P3 500 GB y Kingston A400 480 GB) y `bios_latest.json` (ASRock B450M-HDV R4.0 = P10.10).
- El registro (`Zentra.log`) anota una línea por regla con su resultado, razón y evidencia.

**Qué lee**
- RAM: velocidad configurada y reportada por módulo, banco ("P0 CHANNEL A"), número de parte y RAM instalada (no la visible: la UMA esconde una parte).
- Gráfica: memoria dedicada (UMA) y si es integrada o dedicada.
- Discos: salud, desgaste, errores sin corregir, horas y temperatura, pidiendo el contador a cada disco (la clase no se puede listar directamente).
- BIOS (versión SMBIOS y fecha sin corrimiento de zona horaria) y placa.
- VBS, integridad de memoria, Secure Boot y activación.
- Kernel-Power 41 de los últimos 30 días (sin contar los causados por el botón de encendido), pantallazos azules y minidumps.
- Plan de energía, auto-tuning de TCP, TRIM, pagefile, Storage Sense y número de apps de inicio activas.

**Privacidad**
- El snapshot no guarda seriales, etiquetas de volumen, rutas, nombres de apps, del plan de energía, del equipo ni del usuario. Los textos de hardware se limpian y se recortan a 64 caracteres. Hay tests que lo verifican.

**Resultado en el PC de Diego** (como administrador, 19/11/2026)
- Hallazgos: memoria de la gráfica integrada en 495 MB con 16 GB (Alta), 3 apagados inesperados en 30 días (Alta), Kingston A400 con 1 error de lectura sin corregir (Crítica) y VBS activo (informativo).
- Desgaste: Crucial P3 0 %. Kingston A400: no medible (Windows reporta 0 en SATA aunque el disco esté gastado; necesita smartctl).
- RAM a 3600 igual a la del kit, BIOS P10.10 (la última estable), TRIM activo, plan Equilibrado y Windows activado.

### Fase 1: rebrand y limpieza del fork (19/11/2026)

**Marca**
- El producto se llama **Zentra** y el ejecutable es `Zentra.exe`. Los namespaces de C# siguen siendo `optimizerDuck.*` para poder traer correcciones del original.
- Ícono provisional (una "Z" sobre un degradado) en la ventana, en "Acerca de" y en el `.exe`.
- Carpeta de datos: `%LocalAppData%\Zentra` (registro `Zentra.log`, reversiones e historial).
- Punto de restauración: "Zentra Restore Point".
- "Acerca de", `THIRD-PARTY-NOTICES.md` y el README conservan los créditos a optimizerDuck y fatDuck.

**Actualizador**
- Solo busca versiones en `Dieguito44/optimizerDuck` y solo acepta ejecutables `Zentra*.exe`. Nunca ofrece una versión del original.
- Si el fork aún no tiene releases, lo anota como información y no como error.
- `release.yml` publica `Zentra-Windows-x64-<versión>.exe` y ya no notifica al Discord del original. `CODEOWNERS` apunta al fork.

**Ajustes de la sección 1.1 del plan**
- La optimización de "200+ servicios" (deshabilitaba varios y tocaba `wuauserv` y `WaaSMedicSvc`) se reemplazó por **Servicios poco usados en Manual** (ACT-SERVICES-MANUAL): Fax, MapsBroker, RetailDemo, wisvc y WMPNetworkSvc pasan a Manual, nunca a Deshabilitado. Los que no existen se saltan.
- Se quitó **MaximizeUpdatePauseLimit**: permitía pausar Windows Update sin límite. La pausa controlada de 7 días (ACT-WU-PAUSE7) llega en la Fase 7.
- El README ya no recomienda apagar Windows Update.
- **Desactivar hibernación e inicio rápido** ahora dice que es opcional y que no hace parte del perfil estándar.
- La advertencia del 100 % de CPU con el plan de energía propio (issue #29 del original) ya estaba en inglés y en español.

**Bloatware**
- Edge, WebView2, la Store, winget (DesktopAppInstaller), Seguridad de Windows, la Calculadora, Fotos, VCLibs, UI.Xaml, .NET Native y Windows App Runtime están protegidos. No aparecen en la lista y quitarlos se rechaza aunque alguien lo intente.

**Idioma**
- La app arranca en español (`es-ES`). Se corrigieron tildes y textos de marca.

**Tests**
- `BlacklistTests`: 19 reglas sobre el código fuente de la app (Defender, Windows Update, UAC, firewall, SmartScreen, BitLocker, Smart App Control, bcdedit, auto-tuning de TCP, pagefile, HOSTS, Edge/WebView2, liberadores de RAM y activadores). Cada regla se prueba contra ejemplos malos y deja pasar las variantes controladas.
- Lista blanca de los 6 servicios que hoy se ponen en Disabled (cualquier otro hace fallar el test).
- Tests del actualizador (apunta al fork e ignora ejecutables del original) y de las apps protegidas.
- Total: 844 tests, 0 fallos, 6 omitidos (requieren correr como administrador o un estado concreto del PC).
