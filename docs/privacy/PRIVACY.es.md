# Política de Privacidad

[English](PRIVACY.md) · **Español** · [Português (Brasil)](PRIVACY.pt-BR.md)

**FreePartition** — última actualización: 29 de octubre de 2026

FreePartition es un administrador de particiones de disco gratuito y de código abierto para
Windows. Esta política explica qué hace y qué no hace la aplicación con su información.

## Resumen

FreePartition no recopila, transmite ni vende datos personales. Funciona íntegramente en su
equipo y no tiene funciones de red propias.

## Información que recopilamos

Ninguna. La aplicación no tiene cuentas, analítica, telemetría, publicidad ni informes de
errores. No se envía nada al autor ni a terceros.

## Información que la aplicación lee en su equipo

Para cumplir su función, la aplicación lee información de forma local, solo mientras la usa:

- **Datos de discos, particiones y volúmenes** (tamaños, etiquetas, letras de unidad, sistemas
  de archivos) mediante Windows Management Instrumentation (WMI).
- **Datos de estado del disco** (atributos S.M.A.R.T., registro de salud NVMe, temperatura,
  horas de encendido).
- **Metadatos NTFS y contenido de archivos** de un volumen que usted elija analizar para buscar
  archivos eliminados. Los volúmenes se abren en modo de solo lectura. Los nombres, rutas y
  vistas previas de los archivos solo se muestran en la ventana de la aplicación.

Esta información permanece en su equipo y nunca se envía a ningún lugar.

## Información que la aplicación almacena

La aplicación escribe un único archivo local, el registro de operaciones:

```
%LocalAppData%\FreePartition\operations.log
```

Registra las acciones puestas en cola y aplicadas en la aplicación (por ejemplo, un formateo
o un cambio de tamaño, con la letra de unidad, el tamaño y la hora) y los errores. Puede
abrirlo o eliminarlo en cualquier momento desde el menú Herramientas o en el Explorador de
archivos. Nunca se envía a ningún lugar.

Los archivos que decida recuperar se copian únicamente a la carpeta de destino que seleccione,
en otra unidad.

## Acceso a la red

FreePartition no realiza conexiones de red. La única acción externa es abrir un enlace o una
carpeta (como los enlaces de la ventana Acerca de o la ubicación del registro) con el programa
predeterminado de Windows cuando usted hace clic. Esos sitios se rigen por sus propias
políticas de privacidad.

## Derechos de administrador

La aplicación requiere derechos de administrador (UAC) para acceder a los volúmenes sin
procesar y ejecutar comandos de particionado. Estos derechos se usan únicamente para las
operaciones de disco que usted solicite.

## Privacidad de los menores

La aplicación no recopila datos de ninguna persona, incluidos los menores.

## Cambios en esta política

Si el comportamiento de la aplicación cambia de forma que afecte a la privacidad, este
documento se actualizará y se revisará la fecha indicada arriba. Las versiones anteriores
están disponibles en el historial del repositorio.

## Contacto

Preguntas o inquietudes: abra una incidencia (issue) en
[github.com/rfiori/FreePartition/issues](https://github.com/rfiori/FreePartition/issues).

## Descargo de responsabilidad

FreePartition se ofrece bajo la Licencia MIT, sin garantía. Consulte [LICENSE](../../LICENSE).
Es un proyecto independiente y no está afiliado a EaseUS.
