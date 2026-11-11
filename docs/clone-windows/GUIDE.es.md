# Migrar Windows a otro disco: guía paso a paso

[English](GUIDE.md) · **Español** · [Português (Brasil)](GUIDE.pt-BR.md)

Esta guía es para quien quiere pasar un Windows instalado a un disco nuevo (por ejemplo, de un SSD o disco duro antiguo
a un NVMe M.2) con FreePartition, sin reinstalar nada. No hace falta saber de discos: siga los pasos en orden y, si algo
sale mal, busque el mensaje en [Problemas y soluciones](#7-problemas-y-soluciones).

> **El disco antiguo solo se lee.** Si algo sale mal, vuelva a conectarlo y el equipo arranca como antes.
> El **disco nuevo se borra** por completo.

Cómo escribir un comando: pulse **Windows + X**, elija **Terminal (Administrador)** (o **Windows PowerShell
(Administrador)** / **Símbolo del sistema (Administrador)**), haga clic en **Sí**, escriba el comando y pulse **Enter**.

## Contenido

1. [Antes de empezar](#1-antes-de-empezar)
2. [Crear la memoria USB](#2-crear-la-memoria-usb)
3. [Arrancar el equipo desde la memoria USB](#3-arrancar-el-equipo-desde-la-memoria-usb)
4. [Copiar Windows](#4-copiar-windows)
5. [Primer arranque desde el disco nuevo](#5-primer-arranque-desde-el-disco-nuevo)
6. [Después de la migración](#6-después-de-la-migración)
7. [Problemas y soluciones](#7-problemas-y-soluciones)
8. [El registro: qué enviar al pedir ayuda](#8-el-registro-qué-enviar-al-pedir-ayuda)

## 1. Antes de empezar

Haga estas comprobaciones en el Windows actual, antes de usar la memoria USB.

**1.1 Haga una copia de seguridad** de lo que no puede perder (documentos, fotos) en otro disco o en la nube.

**1.2 Averigüe si el equipo usa UEFI o BIOS.** Pulse **Windows + R**, escriba `msinfo32` y pulse **Enter**. Mire la
línea **Modo de BIOS**:

- **UEFI**: arrancará la memoria USB en modo UEFI.
- **Heredado** (Legacy): arrancará la memoria USB en modo BIOS (legacy).

Anótelo: lo necesitará en el paso 3.

**1.3 Desactive el Inicio rápido.** Con él, "Apagar" no apaga Windows de verdad: guarda una sesión a medias en el disco,
y una copia de eso puede arrancar con errores. Ejecute (como administrador):

```
powercfg /h off
```

Esto desactiva la hibernación y el Inicio rápido. Puede volver a activarlos después de la migración (paso 6.4).

**1.4 Compruebe el tamaño del disco nuevo.** La copia mantiene el tamaño exacto de la partición de Windows (C:). El disco
nuevo debe tener espacio para C: y alrededor de 1 GB más (particiones de arranque y de recuperación). FreePartition
avisa si no cabe; vea [El disco de destino es demasiado pequeño](#el-disco-de-destino-es-demasiado-pequeño).

**1.5 BitLocker / Cifrado del dispositivo** (*no probado*). Si C: está cifrado, WinPE no puede leerlo. Compruébelo:

```
manage-bde -status C:
```

Si **Estado de protección** es **Protección activada** o **Estado de conversión** no es **Descifrado completo**,
guarde su clave de recuperación y descifre la unidad antes de la migración:

```
manage-bde -protectors -get C:
manage-bde -off C:
```

El descifrado tarda. Repita `manage-bde -status C:` hasta que diga **Descifrado completo**. Puede volver a activar
BitLocker después de la migración.

**1.6 Apague por completo.** Cuando esté listo, apague con este comando (nunca usa el Inicio rápido):

```
shutdown /s /t 0
```

## 2. Crear la memoria USB

Necesita una memoria USB de **4 GB o más**. **Todo lo que contenga se borra.**

1. Descargue la imagen de WinPE:
   Abra la **[última release](https://github.com/rfiori/FreePartition/releases/latest)** y, en **Assets**, descargue
   el archivo **`FreePartition-WinPE.iso`**. FreePartition abre la misma página al hacer clic en **Clone Windows** en
   un Windows normal.
2. Grábela en la memoria USB con **uno** de estos programas:
   - **[Rufus](https://rufus.ie)** (recomendado):
     1. Conecte la memoria USB y abra Rufus.
     2. **Dispositivo**: elija la memoria USB (compruebe el tamaño y el nombre: se borrará).
     3. **Elección de arranque**: haga clic en **SELECCIONAR** y elija `FreePartition-WinPE.iso`.
     4. **Esquema de partición**: **MBR**. **Sistema destino**: **BIOS o UEFI**.
     5. **Sistema de archivos**: **FAT32**.
     6. Haga clic en **EMPEZAR**. Si Rufus pregunta cómo grabar la imagen, mantenga la opción recomendada. Espere a que
        diga **PREPARADO**.
   - **[Ventoy](https://www.ventoy.net)**: instale Ventoy en la memoria USB una vez y luego solo copie el archivo ISO.
     Al arrancar, elija `FreePartition-WinPE.iso` en el menú de Ventoy.

## 3. Arrancar el equipo desde la memoria USB

1. **Conecte el disco nuevo** (dentro del equipo o en una caja USB).
2. Conecte la memoria USB y encienda el equipo.
3. Enseguida, pulse varias veces la tecla del **menú de arranque**. Depende del fabricante:

   | Fabricante | Tecla del menú de arranque |
   | --- | --- |
   | Dell, Lenovo, Acer, Toshiba | F12 |
   | HP | F9 (o Esc y luego F9) |
   | ASUS | F8 (sobremesa) o Esc (portátiles) |
   | MSI | F11 |
   | Gigabyte | F12 |
   | ASRock | F11 |
   | Samsung, Sony | Esc / F10 |

   Si ninguna funciona, busque "tecla menú de arranque" y su modelo, o entre en la configuración de la BIOS/UEFI
   (normalmente **Supr** o **F2**) y ponga la memoria USB primero en el orden de arranque.
4. Elija la memoria USB en el modo que anotó en el paso 1.2:
   - **UEFI**: la entrada que empieza por **UEFI:** (por ejemplo *UEFI: SanDisk*).
   - **Heredado**: la entrada **sin** "UEFI".
5. Espere. WinPE arranca (aparece una ventana negra del Símbolo del sistema) y **FreePartition se abre solo**. Puede
   tardar un minuto.

## 4. Copiar Windows

FreePartition está en inglés; los nombres de los botones aparecen aquí tal como se ven en pantalla.

1. En FreePartition, haga clic en **Clone Windows** en la barra de herramientas (o **Tools > Clone Windows to another
   disk...**).
2. **Windows to copy**: elija su Windows. En WinPE la letra normalmente **no es C:**; compruebe el tamaño y el disco.
3. **Copy to**: elija el disco **nuevo**. Compruebe el número, el modelo y el tamaño: **este disco se borrará**.
4. Lea el resumen bajo las listas. Si aparece un recuadro rojo, vea [Problemas y soluciones](#7-problemas-y-soluciones).
5. Marque **I understand that the destination disk will be erased** y haga clic en **Clone Windows**.
6. Espere. Cada paso muestra **OK**, **Skipped** o **Failed**. La copia tarda desde unos minutos hasta más de una hora,
   según el tamaño y la velocidad de los discos. **No apague el equipo.**

| Estado | Significado |
| --- | --- |
| **OK** | El paso funcionó. |
| **Skipped** (omitido) | El paso no era necesario (por ejemplo, el disco antiguo no tiene partición de recuperación) o no se pudo hacer, pero no es esencial. Lea el aviso del final. |
| **Failed** (falló) | La copia se detuvo. El mensaje dice el motivo. El disco antiguo no se modificó; corrija la causa y vuelva a clonar. |

Al final, un mensaje dice que Windows se copió, a veces con **avisos** (*Warning*). Anótelos (o haga una foto) y
búsquelos en [Problemas y soluciones](#7-problemas-y-soluciones).

**Antes de reiniciar, guarde el registro**: en WinPE está en memoria y se pierde al reiniciar el equipo. Vea el
[paso 8](#8-el-registro-qué-enviar-al-pedir-ayuda).

## 5. Primer arranque desde el disco nuevo

1. Cierre FreePartition. En la ventana negra, escriba `wpeutil shutdown` y pulse **Enter** para apagar el equipo.
2. **Desconecte el disco antiguo** en este primer arranque (o, al menos, elija el disco nuevo en el menú de arranque).
   Con los dos discos conectados, el equipo puede arrancar el Windows antiguo, o el nuevo puede recibir otra letra.
3. Retire la memoria USB y encienda el equipo. Si no arranca, use el menú de arranque (paso 3) y elija el
   **Windows Boot Manager** del disco nuevo.
4. Windows arranca como antes: los mismos programas, archivos y configuración.
5. Compruebe que arrancó realmente desde el disco nuevo: pulse **Windows + X**, elija **Administración de discos** y vea
   que **C:** está en el disco nuevo.

## 6. Después de la migración

### 6.1 Compruebe la partición de recuperación

Ejecute como administrador:

```
reagentc /info
```

Si **Estado de Windows RE** es **Enabled** (Habilitado), está bien. Si es **Disabled** (Deshabilitado), ejecute:

```
reagentc /enable
```

Para probarlo: **Inicio > Inicio/apagado**, mantenga **Mayús** y haga clic en **Reiniciar**. Debe aparecer la pantalla
azul *Elegir una opción* (haga clic en **Continuar** para volver a Windows). Si `reagentc /enable` falla, vea
[La partición de recuperación se copió, pero no se registró](#la-partición-de-recuperación-se-copió-pero-no-se-registró).

### 6.2 Use el espacio extra del disco nuevo

La partición de Windows mantiene su tamaño anterior; el resto del disco nuevo queda sin asignar (libre).

Si el espacio libre está **justo después de C:**, basta con seleccionar C: en FreePartition, hacer clic en **Resize
partition**, hacer que ocupe el espacio libre y hacer clic en **Apply**.

Si la **partición de recuperación está entre C: y el espacio libre** (el caso habitual), primero hay que moverla al final
(*no probado*):

1. Desactive la recuperación un momento (como administrador): `reagentc /disable`
2. En FreePartition, seleccione la partición de recuperación y haga clic en **Show partition** (necesita una letra para
   moverse).
3. Haga clic en **Move partition** y muévala al final del disco.
4. Seleccione C:, haga clic en **Resize partition** y haga que ocupe el espacio libre.
5. Haga clic en **Apply** y espere.
6. Seleccione otra vez la partición de recuperación y haga clic en **Hide partition** (quita la letra), luego en
   **Apply**.
7. Vuelva a activar la recuperación: `reagentc /enable` y compruébelo con `reagentc /info` (paso 6.1).

### 6.3 Vuelva a conectar el disco antiguo (opcional)

Use el disco nuevo unos días. Cuando esté seguro de que todo funciona, vuelva a conectar el disco antiguo si quiere
reutilizarlo y solo entonces formatéelo. Hasta que lo formatee, es su copia de seguridad.

### 6.4 Vuelva a activar lo que desactivó

- Hibernación e Inicio rápido (si los quiere): `powercfg /h on`
- BitLocker (si lo usaba): **Configuración > Privacidad y seguridad > Cifrado del dispositivo**, o
  **Panel de control > Cifrado de unidad BitLocker**.

## 7. Problemas y soluciones

Los mensajes de FreePartition aparecen en inglés; aquí están copiados tal como se ven en pantalla.

### La partición de recuperación se copió, pero no se registró

**Mensaje:** *The recovery partition was copied, but it could not be registered in the copied Windows
(reagentc.exe ...). After starting from the new disk, open a Command Prompt as administrator and run: reagentc /enable*

**Las versiones anteriores mostraban:** *The recovery partition was not copied: The term 'reagentc.exe' is not
recognized...*, con el paso de recuperación **Skipped**.

**Causa:** los archivos se copiaron, pero no se indicó al Windows copiado dónde está su partición de recuperación (WinPE
no tiene `reagentc.exe`). Windows funciona con normalidad; solo *Restablecer este PC* y las opciones de reparación
quedan desactivadas.

**Solución:** después de arrancar desde el disco nuevo, ejecute como administrador:

```
reagentc /enable
reagentc /info
```

Si `reagentc /enable` dice que no se encontró la imagen de Windows RE, dé una letra temporal a la partición de
recuperación y apunte Windows a ella:

```
diskpart
list disk
select disk 0
list partition
select partition 4
assign letter=R
exit
```

Cambie `0` por el número del disco nuevo y `4` por la partición **Recuperación** (de unos 500 MB a 1 GB). Después:

```
reagentc /setreimage /path R:\Recovery\WindowsRE /target C:\Windows
reagentc /enable
```

Y quite la letra:

```
diskpart
select disk 0
select partition 4
remove letter=R
exit
```

Pruébelo con **Mayús + Reiniciar** (paso 6.1).

### La partición de recuperación no se copió

**Mensaje:** *The recovery partition was not copied: ... Windows starts without it; the "Reset this PC" and repair
options will not work until it is recreated.*

**Causa:** falló la creación o la copia de la partición de recuperación. El resto de la migración está bien.

**Solución:** Windows funciona. Si necesita *Restablecer este PC*, conserve el disco antiguo (todavía tiene la partición
de recuperación) o cree un medio de recuperación: **Panel de control > Recuperación > Crear una unidad de recuperación**.

### bcdboot no pudo grabar los archivos de arranque

**Mensaje:** *bcdboot could not write the boot files (...). The boot files of the source were copied and its boot store
was repointed to the new disk instead.*

**Causa:** el `bcdboot` de WinPE rechazó el Windows copiado. Ocurre con versiones antiguas de Windows (como Windows 7)
copiadas desde un WinPE reciente.

**Solución:** no hay que hacer nada: FreePartition usó los archivos de arranque del disco antiguo. Pruebe a arrancar
desde el disco nuevo (paso 5). Si no arranca, vea
[El equipo no arranca desde el disco nuevo](#el-equipo-no-arranca-desde-el-disco-nuevo).

### El equipo no arranca desde el disco nuevo

**Síntomas:** *No bootable device*, *Operating system not found*, pantalla negra, o el equipo entra directamente en la
configuración de la BIOS.

Compruebe, en este orden:

1. **Modo:** un Windows que era **UEFI** (paso 1.2) necesita el equipo en modo UEFI; **Heredado** necesita el modo
   BIOS/CSM. Busque en la configuración de la BIOS *Boot Mode*, *CSM* o *Legacy support*.
2. **Orden de arranque:** en el menú de arranque, elija **Windows Boot Manager** (UEFI) o el nombre del disco nuevo
   (BIOS). Si hay dos entradas *Windows Boot Manager*, pruebe la que tiene el nombre del disco nuevo.
3. **Vuelva a grabar los archivos de arranque.** Arranque otra vez desde la memoria USB, cierre FreePartition y, en la
   ventana negra, escriba:

   ```
   diskpart
   list disk
   select disk 1
   list partition
   select partition 1
   assign letter=S
   list volume
   exit
   ```

   - `select disk 1`: el número del disco **nuevo**.
   - `select partition 1`: la partición **Sistema** (unos 100 MB en UEFI, *Reservado para el sistema* en BIOS).
   - En `list volume`, anote la letra del Windows copiado (el volumen NTFS grande del disco nuevo, por ejemplo **E**).

   Después, para **UEFI**:

   ```
   bcdboot E:\Windows /s S: /f UEFI
   ```

   Para **BIOS (Heredado)**, marque también la partición como activa:

   ```
   diskpart
   select disk 1
   select partition 1
   active
   exit
   bcdboot E:\Windows /s S: /f BIOS
   ```

   Escriba `wpeutil reboot` para reiniciar. Para abrir FreePartition otra vez sin reiniciar, escriba
   `X:\FreePartition\FreePartition.exe`.
4. Si nada funciona, vuelva a conectar el disco antiguo: no se modificó y arranca como antes. Después, vuelva a clonar.

### El Windows nuevo muestra errores de disco, una comprobación de disco o una sesión antigua

**Causa:** Windows no se apagó por completo (el Inicio rápido o la hibernación estaban activados).

**Solución:** arranque desde el disco antiguo, haga los pasos 1.3 y 1.6 (desactivar el Inicio rápido, apagar con
`shutdown /s /t 0`) y vuelva a clonar.

### Windows no aparece en "Windows to copy"

**Mensaje:** *No installed Windows was found. A Windows partition needs a drive letter to be found...*

**Causa:** en WinPE, la partición de Windows quedó sin letra, o está cifrada (BitLocker).

**Solución:** en la ventana principal, seleccione la partición de Windows (la NTFS grande), haga clic en **Show
partition** (le da una letra) y en **Apply**, haga clic en **Refresh** y abra **Clone Windows** otra vez. Si está
cifrada, vea el paso 1.5.

### Solo hay un disco / el disco nuevo no aparece

**Mensaje:** *There is only one disk. Connect the destination disk, then refresh the disks.*

- Compruebe el cable o la caja USB y haga clic en **Refresh**.
- **Disco NVMe invisible en WinPE** (*no probado*): muchos portátiles (Intel de 11.ª generación en adelante, Dell, HP,
  Lenovo) usan **Intel RST / VMD**, y WinPE no tiene controlador para ello. En la configuración de la BIOS, cambie
  **SATA Operation** (o *Storage mode*) de **RAID** / **Intel RST** a **AHCI**, o desactive **VMD Controller**. Hágalo
  **solo mientras usa la memoria USB** y **vuelva a dejarlo como estaba antes de arrancar Windows**: si no, Windows se
  detiene con *INACCESSIBLE_BOOT_DEVICE*.

### El disco de destino es demasiado pequeño

**Mensaje:** *Disk N is too small: the copy needs X and the disk has Y. Shrink the Windows partition in the main window
first (Resize), apply it, then open this window again.*

**Solución:** primero libere espacio en C: (borre archivos o muévalos a otro disco). Después, en FreePartition,
seleccione C:, haga clic en **Resize partition**, elija un tamaño menor que el disco nuevo menos unos 2 GB y haga clic
en **Apply**. Abra **Clone Windows** otra vez.

### Hay operaciones pendientes

**Mensaje:** *There are pending operations. Apply or undo them first...*

**Solución:** en la ventana principal, haga clic en **Apply** (para ejecutarlas) o en **Undo** hasta que la lista quede
vacía.

### La memoria USB no aparece en el menú de arranque, o no arranca

- Grábela otra vez con Rufus con las opciones del paso 2 (MBR, BIOS o UEFI, FAT32).
- Pruebe otro puerto USB (en equipos de sobremesa, uno de la parte trasera).
- En la configuración de la BIOS, active **USB boot** y desactive **Fast Boot**.
- **Ventoy con Secure Boot:** la primera vez, Ventoy muestra una pantalla azul *Verification failed*; siga los pasos de
  *Enroll key* de Ventoy, o use Rufus.

### FreePartition no se abre en WinPE

En la ventana negra, escriba:

```
X:\FreePartition\FreePartition.exe
```

Si aun así no se abre, haga una foto del mensaje e infórmelo (paso 8).

### Pide la clave de recuperación de BitLocker (*no probado*)

Escriba la clave que guardó en el paso 1.5 (también está en <https://account.microsoft.com/devices/recoverykey> si su
cuenta Microsoft se usa en el equipo).

### Cancelado o interrumpido

**Mensaje:** *Canceled. The destination disk was erased and is incomplete; run the clone again before using it.*

**Solución:** el disco antiguo no se modificó. Vuelva a clonar.

## 8. El registro: qué enviar al pedir ayuda

Todo lo que hace la clonación se escribe en el cuadro de registro de la ventana y en `operations.log`.

- **En WinPE el registro se pierde al reiniciar el equipo.** Antes de reiniciar, cópielo a la memoria USB o a otro
  disco. En la ventana negra (cambie `D:` por la letra de la memoria USB):

  ```
  copy X:\FreePartition\Log\operations.log D:\
  ```

  O haga clic dentro del cuadro de registro, pulse **Ctrl + A**, **Ctrl + C** y péguelo en un archivo de otro disco.
- En un Windows normal: **Tools > Open log folder**.

Al [abrir una incidencia](https://github.com/rfiori/FreePartition/issues), incluya el registro, los mensajes o una foto
de la pantalla, los discos de origen y destino (modelo, tamaño, SATA o NVMe) y el modo (UEFI o Heredado).
