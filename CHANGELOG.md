# Changelog de Zentra

Zentra es un fork de [optimizerDuck](https://github.com/itsfatduck/optimizerDuck) (GPL v3). Aquí solo se anotan los cambios de Zentra; los del proyecto original están en sus releases.

## [Sin publicar]

### Mejoras, Fase 1C: consentimiento, clave fuera del PC del cliente y acciones irreversibles (19/11/2026, PR 22, sobre el PR 21)

- **Consentimiento Ley 1581 (F1-07, auditoría Z-10):** la tarjeta de IA tiene una casilla obligatoria.
  - **El texto dice** quién recibe los datos (Anthropic, PBC, en EE. UU.), para qué (redactar el informe) y por cuánto tiempo. Según la política de Anthropic, los borra dentro de 30 días, salvo las excepciones que lista. También dice que es voluntario.
  - **Sin la casilla** no se envía nada y el informe sale con la plantilla. El servicio lo comprueba; no depende solo de la pantalla.
  - **session.json guarda** la versión del texto aceptado y la hora. Si el texto cambia, un consentimiento viejo ya no vale.
  - **Minimización:** el paquete ya salía de una lista cerrada de campos. Ahora además cada texto pasa por un redactor que quita el usuario, el nombre del PC, el dominio, la etiqueta del cliente, el serial de la BIOS, las rutas personales, los correos y las IP. Es el mismo redactor de los informes de fallos (F4-05).
- **La clave no toca el PC del cliente (F1-08, auditoría Z-32), opción (a):**
  - **En el PC del cliente** no hay caja para la clave, y `ApiKeyStore` la rechaza si llega. "Exportar datos para la IA" guarda `zentra-ia-solicitud.json` en la carpeta de la sesión: el paquete anónimo, su SHA-256 y el consentimiento.
  - **En tu PC** (Zentra con `--equipo-tecnico`), "Escribir desde archivo" lee la solicitud, comprueba su huella y su consentimiento, llama a la IA y guarda `zentra-ia-informe.json` al lado.
  - **De vuelta en el PC del cliente,** "Importar informe" solo lo acepta si es de esta sesión y de estos mismos datos. Si se midió otra vez después de exportar, se rechaza. Además el texto pasa otra vez por las mismas revisiones que si Zentra hubiera llamado a la IA.
- **Confirmar lo irreversible (F1-09, auditorías Z-06 y Z-07):**
  - **ResetBase** pide un diálogo "No reversible". **Compact OS** pide uno "Difícil de revertir": sí se revierte, pero tarda y necesita espacio.
  - **El motor lo exige:** sin la confirmación, `OptimizationService` no lo ejecuta. Un preset no puede incluir esos ajustes, ni con `allowRisky`.
  - **Quitar apps:** el diálogo de Bloatware y el del perfil dicen "No reversible". Desinstalar un programa en Programas ahora pide confirmación.
  - **CLI:** las apps del preset solo se quitan con `--remove-apps`. `--keep-apps` sigue aceptado, y ya es lo normal.
  - **Puntos de restauración:** Zentra no tiene ninguna función que los borre, así que no hubo nada que confirmar.
- **Tests:** 1793 en total, 0 fallos.
  - Sin consentimiento no hay llamada.
  - El redactor y el paquete limpio.
  - La ida y vuelta por archivos, y su rechazo por otra sesión, otros datos, un archivo editado, un informe que no pasa las revisiones o un archivo enorme.
  - La clave rechazada en el PC del cliente.
  - El motor que no corre lo irreversible sin confirmación, los presets y el CLI.
- **Sin probar en pantalla:** la casilla, los botones nuevos, los diálogos y la ida y vuelta con dos PCs reales. Para probar la IA en tu PC tienes que pegar la clave en la app.

### Mejoras, Fase 1B: estado protegido y diario de intención (19/11/2026, PR 21, sobre el PR 20)

- **Estado protegido (F1-02, auditoría Z-02):** los datos de reversión, el historial y el diario pasan de `%LOCALAPPDATA%\Zentra` a `%ProgramData%\Zentra\Estado`.
  - **La carpeta:** solo SYSTEM y Administradores pueden escribir; Usuarios solo leen y no hay herencia. Si ya existe con otro dueño o es un enlace, Zentra no abre: sin estado seguro no se aplica nada.
  - **La firma:** cada archivo empieza con un HMAC-SHA256. La clave es aleatoria y única por PC, protegida con DPAPI de máquina. Un archivo alterado, o sin firma, se trata como ilegible y se aparta: nunca se revierte un valor que otro cambió.
  - **Migración:** al primer arranque se mueven y firman los archivos de la carpeta anterior. Se confía en ellos esa única vez, porque antes no se podían verificar; solo afecta a PCs que ya usaron Zentra, como el de Diego.
- **Diario de intención (F1-03, auditoría Z-09):** antes, los datos de reversión llegaban al disco solo al final de cada ajuste. Si Zentra se cerraba a mitad, los cambios ya hechos quedaban sin vuelta atrás.
  - **Ahora:** cada cambio con reversión se escribe en el diario apenas ocurre, firmado y forzado a disco. El diario se borra cuando el ajuste guarda su reversión.
  - **Al siguiente arranque:** lo que quedó en el diario se convierte en datos de reversión y un aviso lista esos ajustes, para revertirlos o aplicarlos de nuevo.
  - **Cancelar a mitad** ahora también guarda la reversión de lo ya hecho.
- **Tests:** 1770 en total, 0 fallos.
  - Firma y rechazo de archivos alterados (reversión, historial, diario), migración y la ACL.
  - Un ajuste sin terminar que se recupera.
  - Una prueba solo para el CI que crea la carpeta protegida de verdad en ProgramData.
  - Las pruebas guardan su estado en una carpeta temporal propia.
- **Sin probar en el PC real:** el primer arranque, que crea la carpeta y mueve tus datos actuales. Ocurre cuando abras Zentra.

### Mejoras, Fase 1A: procesos y archivos endurecidos (19/11/2026, PR 20, sobre el PR 19)

Primer bloque de la Fase 1 (bloqueantes legales y de seguridad).

- **Mínimo privilegio en el registro (F1-04):**
  - El PR #100 de upstream y su versión para `DeleteValue` ya estaban en Zentra.
  - Quedaban el Gestor de Inicio, que abría claves con escritura completa, y la raíz `HKEY_USERS\<SID>` del cliente. Ahora piden solo leer y poner valores, o crear subclaves.
- **Rutas absolutas (F1-05, auditoría Z-05):** `SystemTools` da la ruta absoluta de cada herramienta de Windows. Un nombre que no conoce se rechaza; no se busca en el PATH.
  - **Cada proceso hijo** arranca en System32, con un PATH y un `PSModulePath` que solo tienen carpetas de Windows. Así un `compact` o un módulo de PowerShell plantado por otro usuario no se ejecuta como administrador.
  - **winget** sale del paquete App Installer, no del alias en la carpeta del usuario, y solo si lo firmó Microsoft.
  - **Verificado en el PC de Diego:** winget 1.29 corre desde su paquete y el PATH del hijo es solo Windows.
- **Limpieza sin enlaces (F1-06, auditoría Z-03):** comprobé que la recursión de .NET 10 **sí** sigue los junctions. Un enlace dentro de `%TEMP%` hacía que la limpieza, elevada, borrara archivos de otra carpeta.
  - Ahora la limpieza nunca baja por un enlace ni lo borra, no limpia una carpeta que sea un enlace y registra cuántos dejó.
  - Lo prueba un test con un junction real.
- **Logs (F1-11):**
  - **Nivel:** Information por defecto. Verbose solo con `App.VerboseLogs` o `ZENTRA_LOG_VERBOSE=1`.
  - **Rotación:** un archivo por día y uno nuevo al pasar de 5 MB. Se guardan 10 como máximo, así que ocupan 50 MB a lo sumo.
  - **Secretos:** se siguen redactando en cada línea.
- **Tests:** 1753 en total, 0 fallos. Un test impide lanzar procesos por un nombre suelto.

### Mejoras, Fase 4C: reportes de fallos, velocidad y garantía (19/11/2026, PR 19, sobre el PR 18)

Cierra la Fase 4.

- **Reportes de fallos (F4-05):** opción en Configuración, **apagada por defecto**.
  - **Encendida:** cada fallo deja además un reporte con la versión, Windows, la excepción y la pila. No lleva el nombre del usuario ni del PC, la carpeta del usuario, correos, direcciones IP ni claves.
  - **Zentra nunca lo envía:** "Copiar el último reporte" lo pone en el portapapeles y el técnico lo manda por donde quiera. Así no se crea una transferencia automática de datos (Ley 1581).
  - **Por qué no un issue de GitHub:** el fork tiene los issues apagados, y activarlos es decisión de Diego.
  - **Límite conocido:** una IP privada que empiece por `10.0.` se conserva, porque se confunde con la versión de Windows.
- **Velocidad de internet (F4-06):** tarjeta en Diagnóstico que mide bajada, subida y latencia contra la prueba pública de Cloudflare (speed.cloudflare.com).
  - **Consentimiento:** antes de medir dice que Cloudflare verá la IP pública y que gasta unos 45 MB de bajada y 10 MB de subida.
  - **Resultado:** cada cifra es la mediana de varias pruebas. Si el servicio no responde, la prueba es "no medible".
- **Garantía (F4-07):** las APIs de los fabricantes piden claves, así que Zentra no las consulta.
  - **Qué hace:** muestra marca, modelo y serial, copia el serial y abre la página oficial de garantía (Lenovo, HP, Dell, ASUS) para pegarlo ahí. El serial no va en la dirección ni en el informe.
  - **PC armado por partes:** si no tiene serial, Zentra lo dice.
- **Tests:** 1737 en total, 0 fallos.
- **Sin probar en el PC real:** la prueba de velocidad, porque gasta datos y expone la IP.

### Mejoras, Fase 4B: prueba térmica corta y latencia DPC (19/11/2026, PR 18, sobre el PR 17)

- **Prueba térmica (F4-02, riesgo moderado):** botón "Prueba térmica (90 s)" en la tarjeta de Temperatura del Diagnóstico.
  - **Antes:** confirmación siempre. PawnIO se instala solo para la prueba, con consentimiento, y se quita al final.
  - **La carga:** pone a trabajar todos los núcleos y lee la temperatura cada segundo.
  - **Se detiene sola** si llega a 95 °C, si el portátil se desconecta del cargador o si el sensor deja de responder 3 s.
  - **No empieza** con batería, sin sensor o si ya está en el límite en reposo. Se puede cancelar.
  - **Resultado:** temperatura en reposo, máxima, segundos de carga y cuántas veces frenó el firmware durante la prueba (evento 37). No cambia ninguna configuración ni toca la temperatura del diagnóstico.
- **Latencia DPC (F4-03):** Zentra no la mide. Una tarjeta explica cuándo sirve y abre la página oficial de LatencyMon, sin elevar.
  - Antes de usarlo en un servicio pagado hay que revisar su licencia: el sitio vende licencias para técnicos.
- **Tests:** 1718 en total, 0 fallos. Las reglas de seguridad se prueban con una máquina simulada.
- **Sin probar en el PC real:** la prueba térmica, porque calienta el PC.

### Mejoras, Fase 4A: drivers y energía, solo informar (19/11/2026, PR 17, sobre el PR 16)

Primer bloque de la Fase 4, que es opcional.

- **Drivers (F4-01):**
  - **R-DEVICE-PROBLEM (Media):** dispositivos con código de problema, casi siempre el 28 (sin driver). No cuentan los deshabilitados a propósito (22) ni los desconectados (45).
  - **R-DRIVERS-WU (Baja):** drivers que Windows Update ofrece como actualizaciones opcionales. Se consultan con el agente de Windows Update, la misma búsqueda de "Buscar actualizaciones": no instala nada. Si Windows Update no responde, es "no medible", nunca "al día".
- **Energía (F4-04), R-POWER-MODE:** lee los planes, su origen (Windows, Zentra, fabricante o propio) y el modo de energía de Windows. El modo solo cuenta con el plan Equilibrado.
  - **Baja:** el modo está en "máxima eficiencia" con el PC conectado o en un escritorio.
  - **Info, sin puntos:** se listan los planes del fabricante.
- **Catálogo:** 3 acciones manuales (50) y 3 reglas (39).
- **Verificado en el PC de Diego** (solo lectura, solo conteos):
  - 0 dispositivos con problemas;
  - 5 drivers ofrecidos por Windows Update (37 s de búsqueda);
  - 3 planes de Windows;
  - modo de máximo rendimiento, conectado.
- **Tests:** 1710 en total, 0 fallos.

### Mejoras, Fase 3D: seguimiento, segunda opinión e idiomas (19/11/2026, PR 16, sobre el PR 15)

Cierra la Fase 3.

- **Segunda corrección de la vista previa (también en los PR 13, 14 y 15).** Cinco ajustes cambiaban el PC de verdad al previsualizar un preset, porque llaman a servicios propios fuera de las primitivas con guarda:
  - monitores a su frecuencia máxima;
  - excluir carpetas del índice;
  - desactivar la hibernación;
  - desactivar el ahorro de energía USB;
  - puntos de restauración semanales, que encendían la protección del sistema.

  Ahora solo describen el cambio. El test de vista previa exige guarda a toda llamada a un servicio que no sea una de las cuatro primitivas.
- **Seguimiento a 7 y 30 días (F3-05):** botón "Programar seguimiento" en Sesión, con consentimiento escrito del cliente.
  - **Qué hace:** una tarea como SYSTEM corre solo el diagnóstico, sin cambios, a los 7 y a los 30 días. Guarda el resultado en `%ProgramData%\Zentra\Seguimiento` y se borra sola tras la segunda revisión.
  - **La carpeta:** tiene su propia ACL (SYSTEM y Administradores con control total, Usuarios solo lectura, sin herencia). Se rechaza si ya existía con otro dueño o es un enlace. El ejecutable se descomprime dentro de ella, no en la carpeta temporal.
  - **Quitarlo:** "Quitar seguimiento" o `--remove-followup`.
- **Segunda opinión con Microsoft Safety Scanner (F3-08):** botón en Reparar.
  - Descarga solo desde microsoft.com.
  - Exige la firma Authenticode de Microsoft con cadena hasta una raíz de Microsoft, y mantiene el archivo bloqueado.
  - Corre `/Q /N`: solo detecta, no quita nada.
  - Copia el log de ese análisis. Si no puede leer el resultado, dice "no medible", nunca "limpio".
  - Borra el escáner al final.
- **Idiomas (F3-11):**
  - El español pasa a `Translations.es.resx` (neutro) y el idioma por defecto es **es-CO**, con fechas y números de Colombia.
  - Una configuración guardada con es-ES se resuelve sola.
  - **Tests nuevos:** cada idioma tiene todas las claves y ningún marcador `{n}` de más; cada idioma soportado tiene su archivo; cada clave literal del código existe.
  - Esa revisión encontró 20 claves inexistentes en un diálogo heredado de optimizerDuck. Nada abre ese diálogo, así que el test lo exime.
- **Tests:** 1682 en total, 0 fallos.
- **Sin probar en el PC real:**
  - el seguimiento, porque crea una tarea en el PC;
  - el Safety Scanner, porque descarga unos 200 MB;
  - la pantalla con es-CO.

### Mejoras, Fase 3C: revisiones de solo lectura y una corrección de la 3A (19/11/2026, PR 15, sobre el PR 14)

- **Corrección importante (también en los PR 13 y 14):** con presets, cualquier ajuste se puede previsualizar, no solo los del perfil.
  - **El problema:** la limpieza de componentes de DISM y "/ResetBase" se ejecutaban **de verdad** durante la vista previa, porque usan un comando sin ensayo. Pasaba aunque el técnico cancelara.
  - **El arreglo:** ahora la vista previa solo muestra el comando.
  - **Consentimiento escrito (Fase 7):** ya no abre su ventana en la vista previa; se pide al aplicar.
  - **Test nuevo sobre todos los ajustes.** Falla si alguno escribe directo, abre procesos o pide consentimiento sin guarda de vista previa, o si usa un comando de consulta que no sea de solo lectura.
- **ReTrim de SSD (F3-07):** nuevo ajuste en Almacenamiento, "Optimizar los SSD ahora (ReTrim)". Corre `Optimize-Volume -ReTrim` en cada volumen fijo SSD o NVMe con letra.
  - No cambia ninguna configuración.
  - Solo pasan a PowerShell letras de la A a la Z.
  - El estado de TRIM y de Storage Sense ya se leía (R-TRIM-OFF), y ambos se activan con el perfil.
- **DNS (F3-10), regla R-DNS-REVIEW (Media):** marca los DNS **escritos a mano** que no son la red local ni un proveedor público conocido (Google, Cloudflare, Quad9, OpenDNS, AdGuard, CleanBrowsing).
  - Los DNS que da la red (router o proveedor) nunca se marcan.
  - "Desconocido" significa "revisar", no "malicioso".
  - Zentra no cambia DNS ni toca HOSTS.
- **Navegadores (F3-09):** lee Chrome, Edge, Brave y Firefox de la cuenta del cliente, más las directivas de la máquina y del usuario.
  - **R-BROWSER-EXT (Media):** extensiones activas fuera de la tienda, forzadas por directiva o sin empaquetar. Las de la tienda con acceso a todos los sitios se listan sin restar puntos.
  - **R-BROWSER-SEARCH (Media):** buscador predeterminado poco común. Lo acompañan las páginas de inicio, solo como nombre de host.
  - El buscador de Firefox no se lee: está en un archivo comprimido.
- **Privacidad:** nombres de extensiones, hosts y direcciones DNS quedan en el informe del técnico. A la IA solo llegan los conteos (nuevo `EvidenceFact.LocalOnly`).
- **Catálogo:** 3 acciones nuevas (47) y 3 reglas nuevas (36).
- **Verificado en el PC de Diego, solo lectura y mostrando solo conteos:**
  - DNS: uno, puesto a mano, de un proveedor conocido.
  - Extensiones: 3 en Chrome, 2 en Edge y 1 en Brave, todas de la tienda.
  - Buscadores: los predeterminados.
  - Esa prueba encontró dos errores, ya corregidos y con test: registros sobrantes sin `location` y la mezcla de Preferences con Secure Preferences.
- **Tests:** 1642 en total, 0 fallos.

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
