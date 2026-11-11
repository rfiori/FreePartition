# Migrar o Windows para outro disco: guia passo a passo

[English](GUIDE.md) · [Español](GUIDE.es.md) · **Português (Brasil)**

Este guia é para quem quer passar um Windows instalado para um disco novo (por exemplo, de um SSD ou HD antigo para um
NVMe M.2) com o FreePartition, sem reinstalar nada. Você não precisa entender de discos: siga os passos na ordem e, se
algo der errado, procure a mensagem em [Problemas e soluções](#7-problemas-e-soluções).

> **O disco antigo só é lido.** Se algo der errado, reconecte-o e o computador inicia como antes.
> O **disco novo é apagado** por completo.

Como digitar um comando: pressione **Windows + X**, escolha **Terminal (Administrador)** (ou **Windows PowerShell
(Administrador)** / **Prompt de Comando (Administrador)**), clique em **Sim**, digite o comando e pressione **Enter**.

## Conteúdo

1. [Antes de começar](#1-antes-de-começar)
2. [Criar o pendrive](#2-criar-o-pendrive)
3. [Iniciar o computador pelo pendrive](#3-iniciar-o-computador-pelo-pendrive)
4. [Copiar o Windows](#4-copiar-o-windows)
5. [Primeira inicialização pelo disco novo](#5-primeira-inicialização-pelo-disco-novo)
6. [Depois da migração](#6-depois-da-migração)
7. [Problemas e soluções](#7-problemas-e-soluções)
8. [O log: o que enviar ao pedir ajuda](#8-o-log-o-que-enviar-ao-pedir-ajuda)

## 1. Antes de começar

Faça estas verificações no Windows atual, antes de usar o pendrive.

**1.1 Faça backup** do que você não pode perder (documentos, fotos) em outro disco ou na nuvem.

**1.2 Descubra se o computador usa UEFI ou BIOS.** Pressione **Windows + R**, digite `msinfo32` e pressione **Enter**.
Veja a linha **Modo da BIOS**:

- **UEFI**: você vai iniciar o pendrive em modo UEFI.
- **Herdado** (Legacy): você vai iniciar o pendrive em modo BIOS (legacy).

Anote: você vai precisar disso no passo 3.

**1.3 Desligue a Inicialização Rápida.** Com ela, "Desligar" não desliga o Windows de verdade: ele salva uma sessão pela
metade no disco, e uma cópia disso pode iniciar com erros. Execute (como administrador):

```
powercfg /h off
```

Isso desliga a hibernação e a Inicialização Rápida. Você pode ligá-las de novo depois da migração (passo 6.4).

**1.4 Confira o tamanho do disco novo.** A cópia mantém o tamanho exato da partição do Windows (C:). O disco novo
precisa comportar o C: e mais cerca de 1 GB (partições de boot e de recuperação). O FreePartition avisa se não couber;
veja [O disco de destino é pequeno demais](#o-disco-de-destino-é-pequeno-demais).

**1.5 BitLocker / Criptografia do dispositivo** (*não testado*). Se o C: estiver criptografado, o WinPE não consegue
lê-lo. Verifique:

```
manage-bde -status C:
```

Se **Status da Proteção** for **Proteção Ativada** ou **Status da Conversão** não for **Totalmente Descriptografado**,
guarde a sua chave de recuperação e descriptografe a unidade antes da migração:

```
manage-bde -protectors -get C:
manage-bde -off C:
```

A descriptografia demora. Repita `manage-bde -status C:` até aparecer **Totalmente Descriptografado**. Você pode ligar
o BitLocker de novo depois da migração.

**1.6 Desligue por completo.** Quando estiver pronto, desligue com este comando (ele nunca usa a Inicialização Rápida):

```
shutdown /s /t 0
```

## 2. Criar o pendrive

Você precisa de um pendrive de **4 GB ou mais**. **Tudo o que estiver nele é apagado.**

1. Baixe a imagem do WinPE:
   Abra a **[última release](https://github.com/rfiori/FreePartition/releases/latest)** e, em **Assets**, baixe o
   arquivo **`FreePartition-WinPE.iso`**. O FreePartition abre a mesma página quando você clica em **Clone Windows**
   num Windows normal.
2. Grave-a no pendrive com **um** destes programas:
   - **[Rufus](https://rufus.ie)** (recomendado):
     1. Conecte o pendrive e abra o Rufus.
     2. **Dispositivo**: escolha o pendrive (confira o tamanho e o nome: ele será apagado).
     3. **Seleção de boot**: clique em **SELECIONAR** e escolha `FreePartition-WinPE.iso`.
     4. **Esquema de partição**: **MBR**. **Sistema de destino**: **BIOS ou UEFI**.
     5. **Sistema de arquivos**: **FAT32**.
     6. Clique em **INICIAR**. Se o Rufus perguntar como gravar a imagem, mantenha a opção recomendada. Espere aparecer
        **PRONTO**.
   - **[Ventoy](https://www.ventoy.net)**: instale o Ventoy no pendrive uma vez e depois só copie o arquivo ISO para ele.
     Ao iniciar, escolha `FreePartition-WinPE.iso` no menu do Ventoy.

## 3. Iniciar o computador pelo pendrive

1. **Conecte o disco novo** (dentro do computador ou num case USB).
2. Conecte o pendrive e ligue o computador.
3. Logo em seguida, pressione várias vezes a tecla do **menu de boot**. Ela depende do fabricante:

   | Fabricante | Tecla do menu de boot |
   | --- | --- |
   | Dell, Lenovo, Acer, Toshiba | F12 |
   | HP | F9 (ou Esc e depois F9) |
   | ASUS | F8 (desktops) ou Esc (notebooks) |
   | MSI | F11 |
   | Gigabyte | F12 |
   | ASRock | F11 |
   | Samsung, Sony | Esc / F10 |

   Se nenhuma funcionar, pesquise "tecla menu de boot" e o seu modelo, ou entre no setup da BIOS/UEFI (geralmente
   **Del** ou **F2**) e coloque o pendrive em primeiro na ordem de boot.
4. Escolha o pendrive no modo que você anotou no passo 1.2:
   - **UEFI**: a entrada que começa com **UEFI:** (por exemplo *UEFI: SanDisk*).
   - **Herdado**: a entrada **sem** "UEFI".
5. Aguarde. O WinPE inicia (aparece uma janela preta do Prompt de Comando) e **o FreePartition abre sozinho**. Pode
   levar um minuto.

## 4. Copiar o Windows

O FreePartition é em inglês; os nomes dos botões abaixo estão como aparecem na tela.

1. No FreePartition, clique em **Clone Windows** na barra de ferramentas (ou **Tools > Clone Windows to another disk...**).
2. **Windows to copy**: escolha o seu Windows. No WinPE a letra geralmente **não é C:**; confira o tamanho e o disco.
3. **Copy to**: escolha o disco **novo**. Confira o número, o modelo e o tamanho: **este disco será apagado**.
4. Leia o resumo abaixo das listas. Se aparecer uma caixa vermelha, veja [Problemas e soluções](#7-problemas-e-soluções).
5. Marque **I understand that the destination disk will be erased** e clique em **Clone Windows**.
6. Aguarde. Cada passo mostra **OK**, **Skipped** ou **Failed**. A cópia leva de alguns minutos a mais de uma hora,
   conforme o tamanho e a velocidade dos discos. **Não desligue o computador.**

| Status | Significado |
| --- | --- |
| **OK** | O passo funcionou. |
| **Skipped** (pulado) | O passo não era necessário (por exemplo, o disco antigo não tem partição de recuperação) ou não pôde ser feito, mas não é essencial. Leia o aviso no final. |
| **Failed** (falhou) | A cópia parou. A mensagem diz o motivo. O disco antigo não foi alterado; corrija a causa e rode a clonagem de novo. |

No final, uma mensagem diz que o Windows foi copiado, às vezes com **avisos** (*Warning*). Anote-os (ou tire uma foto) e
procure-os em [Problemas e soluções](#7-problemas-e-soluções).

**Antes de reiniciar, salve o log**: no WinPE ele fica na memória e se perde quando o computador reinicia. Veja o
[passo 8](#8-o-log-o-que-enviar-ao-pedir-ajuda).

## 5. Primeira inicialização pelo disco novo

1. Feche o FreePartition. Na janela preta, digite `wpeutil shutdown` e pressione **Enter** para desligar o computador.
2. **Desconecte o disco antigo** nesta primeira inicialização (ou, pelo menos, escolha o disco novo no menu de boot).
   Com os dois discos conectados, o computador pode iniciar o Windows antigo, ou o novo pode receber outra letra.
3. Retire o pendrive e ligue o computador. Se não iniciar, use o menu de boot (passo 3) e escolha o
   **Windows Boot Manager** do disco novo.
4. O Windows inicia como antes: os mesmos programas, arquivos e configurações.
5. Confira que ele iniciou mesmo pelo disco novo: pressione **Windows + X**, escolha **Gerenciamento de Disco** e veja
   que o **C:** está no disco novo.

## 6. Depois da migração

### 6.1 Confira a partição de recuperação

Execute como administrador:

```
reagentc /info
```

Se **Status do Windows RE** for **Enabled** (Habilitado), está tudo certo. Se for **Disabled** (Desabilitado), execute:

```
reagentc /enable
```

Para testar: **Iniciar > Energia**, segure **Shift** e clique em **Reiniciar**. Deve aparecer a tela azul *Escolha uma
opção* (clique em **Continuar** para voltar ao Windows). Se `reagentc /enable` falhar, veja
[A partição de recuperação foi copiada, mas não registrada](#a-partição-de-recuperação-foi-copiada-mas-não-registrada).

### 6.2 Use o espaço a mais do disco novo

A partição do Windows mantém o tamanho antigo; o resto do disco novo fica não alocado (livre).

Se o espaço livre vier **logo depois do C:**, basta selecionar o C: no FreePartition, clicar em **Resize partition**,
fazê-lo ocupar o espaço livre e clicar em **Apply**.

Se a **partição de recuperação estiver entre o C: e o espaço livre** (o caso comum), ela precisa ir para o fim antes
(*não testado*):

1. Desligue a recuperação por um momento (como administrador): `reagentc /disable`
2. No FreePartition, selecione a partição de recuperação e clique em **Show partition** (ela precisa de letra para ser
   movida).
3. Clique em **Move partition** e mova-a para o fim do disco.
4. Selecione o C:, clique em **Resize partition** e faça-o ocupar o espaço livre.
5. Clique em **Apply** e aguarde.
6. Selecione de novo a partição de recuperação e clique em **Hide partition** (remove a letra), depois em **Apply**.
7. Ligue a recuperação de novo: `reagentc /enable` e confira com `reagentc /info` (passo 6.1).

### 6.3 Reconecte o disco antigo (opcional)

Use o disco novo por alguns dias. Quando tiver certeza de que está tudo bem, reconecte o disco antigo se quiser
reaproveitá-lo e só então formate-o. Até formatar, ele é o seu backup.

### 6.4 Ligue de novo o que você desligou

- Hibernação e Inicialização Rápida (se quiser): `powercfg /h on`
- BitLocker (se usava): **Configurações > Privacidade e segurança > Criptografia do dispositivo**, ou
  **Painel de Controle > Criptografia de Unidade de Disco BitLocker**.

## 7. Problemas e soluções

As mensagens do FreePartition aparecem em inglês; elas estão copiadas aqui como aparecem na tela.

### A partição de recuperação foi copiada, mas não registrada

**Mensagem:** *The recovery partition was copied, but it could not be registered in the copied Windows
(reagentc.exe ...). After starting from the new disk, open a Command Prompt as administrator and run: reagentc /enable*

**Versões anteriores mostravam:** *The recovery partition was not copied: The term 'reagentc.exe' is not recognized...*,
com o passo de recuperação **Skipped**.

**Causa:** os arquivos foram copiados, mas o Windows copiado não foi informado de onde está a sua partição de
recuperação (o WinPE não tem o `reagentc.exe`). O Windows funciona normalmente; só o *Restaurar o PC* e as opções de
reparo ficam desligados.

**Solução:** depois de iniciar pelo disco novo, execute como administrador:

```
reagentc /enable
reagentc /info
```

Se o `reagentc /enable` disser que a imagem do Windows RE não foi encontrada, dê uma letra temporária à partição de
recuperação e aponte o Windows para ela:

```
diskpart
list disk
select disk 0
list partition
select partition 4
assign letter=R
exit
```

Troque `0` pelo número do disco novo e `4` pela partição **Recuperação** (cerca de 500 MB a 1 GB). Depois:

```
reagentc /setreimage /path R:\Recovery\WindowsRE /target C:\Windows
reagentc /enable
```

E remova a letra:

```
diskpart
select disk 0
select partition 4
remove letter=R
exit
```

Teste com **Shift + Reiniciar** (passo 6.1).

### A partição de recuperação não foi copiada

**Mensagem:** *The recovery partition was not copied: ... Windows starts without it; the "Reset this PC" and repair
options will not work until it is recreated.*

**Causa:** a criação ou a cópia da partição de recuperação falhou. O resto da migração está certo.

**Solução:** o Windows funciona. Se precisar do *Restaurar o PC*, guarde o disco antigo (ele ainda tem a partição de
recuperação) ou crie uma mídia de recuperação: **Painel de Controle > Recuperação > Criar uma unidade de recuperação**.

### O bcdboot não conseguiu gravar os arquivos de boot

**Mensagem:** *bcdboot could not write the boot files (...). The boot files of the source were copied and its boot store
was repointed to the new disk instead.*

**Causa:** o `bcdboot` do WinPE recusou o Windows copiado. Isso acontece com versões antigas do Windows (como o
Windows 7) copiadas a partir de um WinPE recente.

**Solução:** nada a fazer: o FreePartition usou os arquivos de boot do disco antigo. Teste a inicialização pelo disco
novo (passo 5). Se não iniciar, veja [O computador não inicia pelo disco novo](#o-computador-não-inicia-pelo-disco-novo).

### O computador não inicia pelo disco novo

**Sintomas:** *No bootable device*, *Operating system not found*, tela preta, ou o computador vai direto para o setup da
BIOS.

Verifique, nesta ordem:

1. **Modo:** um Windows que era **UEFI** (passo 1.2) precisa do computador em modo UEFI; **Herdado** precisa do modo
   BIOS/CSM. Procure no setup da BIOS por *Boot Mode*, *CSM* ou *Legacy support*.
2. **Ordem de boot:** no menu de boot, escolha **Windows Boot Manager** (UEFI) ou o nome do disco novo (BIOS). Se houver
   duas entradas *Windows Boot Manager*, tente a que tem o nome do disco novo.
3. **Regrave os arquivos de boot.** Inicie pelo pendrive de novo, feche o FreePartition e, na janela preta, digite:

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

   - `select disk 1`: o número do disco **novo**.
   - `select partition 1`: a partição **Sistema** (cerca de 100 MB no UEFI, *Reservado pelo Sistema* no BIOS).
   - No `list volume`, anote a letra do Windows copiado (o volume NTFS grande do disco novo, por exemplo **E**).

   Depois, para **UEFI**:

   ```
   bcdboot E:\Windows /s S: /f UEFI
   ```

   Para **BIOS (Herdado)**, marque também a partição como ativa:

   ```
   diskpart
   select disk 1
   select partition 1
   active
   exit
   bcdboot E:\Windows /s S: /f BIOS
   ```

   Digite `wpeutil reboot` para reiniciar. Para abrir o FreePartition de novo sem reiniciar, digite
   `X:\FreePartition\FreePartition.exe`.
4. Se nada funcionar, reconecte o disco antigo: ele não foi alterado e inicia como antes. Depois, rode a clonagem de novo.

### O Windows novo mostra erros de disco, verificação de disco ou uma sessão antiga

**Causa:** o Windows não foi desligado por completo (Inicialização Rápida ou hibernação estavam ligadas).

**Solução:** inicie pelo disco antigo, faça os passos 1.3 e 1.6 (desligar a Inicialização Rápida, desligar com
`shutdown /s /t 0`) e rode a clonagem de novo.

### O Windows não aparece em "Windows to copy"

**Mensagem:** *No installed Windows was found. A Windows partition needs a drive letter to be found...*

**Causa:** no WinPE, a partição do Windows ficou sem letra, ou está criptografada (BitLocker).

**Solução:** na janela principal, selecione a partição do Windows (a NTFS grande), clique em **Show partition** (dá uma
letra a ela) e em **Apply**, clique em **Refresh** e abra o **Clone Windows** de novo. Se estiver criptografada, veja o
passo 1.5.

### Só há um disco / o disco novo não aparece

**Mensagem:** *There is only one disk. Connect the destination disk, then refresh the disks.*

- Confira o cabo ou o case USB e clique em **Refresh**.
- **Disco NVMe invisível no WinPE** (*não testado*): muitos notebooks (Intel 11ª geração em diante, Dell, HP, Lenovo)
  usam **Intel RST / VMD**, e o WinPE não tem driver para isso. No setup da BIOS, mude **SATA Operation** (ou *Storage
  mode*) de **RAID** / **Intel RST** para **AHCI**, ou desligue o **VMD Controller**. Faça isso **só enquanto usa o
  pendrive** e **volte a opção antes de iniciar o Windows**: senão o Windows para com *INACCESSIBLE_BOOT_DEVICE*.

### O disco de destino é pequeno demais

**Mensagem:** *Disk N is too small: the copy needs X and the disk has Y. Shrink the Windows partition in the main window
first (Resize), apply it, then open this window again.*

**Solução:** primeiro libere espaço no C: (apague arquivos ou mova-os para outro disco). Depois, no FreePartition,
selecione o C:, clique em **Resize partition**, escolha um tamanho menor que o disco novo menos uns 2 GB e clique em
**Apply**. Abra o **Clone Windows** de novo.

### Há operações pendentes

**Mensagem:** *There are pending operations. Apply or undo them first...*

**Solução:** na janela principal, clique em **Apply** (para executá-las) ou em **Undo** até a lista ficar vazia.

### O pendrive não aparece no menu de boot, ou não inicia

- Grave de novo com o Rufus usando as opções do passo 2 (MBR, BIOS ou UEFI, FAT32).
- Tente outra porta USB (em desktops, uma porta na parte de trás do computador).
- No setup da BIOS, ligue o **USB boot** e desligue o **Fast Boot**.
- **Ventoy com Secure Boot:** na primeira vez, o Ventoy mostra uma tela azul *Verification failed*; siga os passos de
  *Enroll key* do Ventoy, ou use o Rufus.

### O FreePartition não abre no WinPE

Na janela preta, digite:

```
X:\FreePartition\FreePartition.exe
```

Se ainda assim não abrir, tire uma foto da mensagem e informe (passo 8).

### Pede a chave de recuperação do BitLocker (*não testado*)

Digite a chave que você guardou no passo 1.5 (ela também está em <https://account.microsoft.com/devices/recoverykey>
se a sua conta Microsoft é usada no computador).

### Cancelado ou interrompido

**Mensagem:** *Canceled. The destination disk was erased and is incomplete; run the clone again before using it.*

**Solução:** o disco antigo não foi alterado. Rode a clonagem de novo.

## 8. O log: o que enviar ao pedir ajuda

Tudo o que a clonagem faz é gravado na caixa de log da janela e no `operations.log`.

- **No WinPE o log se perde quando o computador reinicia.** Antes de reiniciar, copie-o para o pendrive ou outro disco.
  Na janela preta (troque `D:` pela letra do pendrive):

  ```
  copy X:\FreePartition\Log\operations.log D:\
  ```

  Ou clique dentro da caixa de log, pressione **Ctrl + A**, **Ctrl + C** e cole num arquivo em outro disco.
- Num Windows normal: **Tools > Open log folder**.

Ao [abrir uma issue](https://github.com/rfiori/FreePartition/issues), inclua o log, as mensagens ou uma foto da tela,
os discos de origem e de destino (modelo, tamanho, SATA ou NVMe) e o modo (UEFI ou Herdado).
