# Política de Privacidade

[English](PRIVACY.md) · [Español](PRIVACY.es.md) · **Português (Brasil)**

**FreePartition** — última atualização: 11 de novembro de 2026

O FreePartition é um gerenciador de partições de disco gratuito e de código aberto para
Windows. Esta política explica o que o aplicativo faz, e o que não faz, com as suas informações.

## Resumo

O FreePartition não coleta, transmite nem vende dados pessoais. Ele funciona inteiramente no
seu computador. Seu único recurso de rede é uma verificação opcional de nova versão no GitHub
(veja [Acesso à rede](#acesso-à-rede)).

## Informações que coletamos

Nenhuma. O aplicativo não tem contas, análises de uso, telemetria, publicidade nem relatórios
de falhas. Nada é enviado ao autor ou a terceiros.

## Informações que o aplicativo lê no seu computador

Para realizar suas funções, o aplicativo lê informações localmente, apenas enquanto você o usa:

- **Detalhes de discos, partições e volumes** (tamanhos, rótulos, letras de unidade, sistemas
  de arquivos) por meio do Windows Management Instrumentation (WMI).
- **Dados de saúde do disco** (atributos S.M.A.R.T., registro de saúde NVMe, temperatura,
  tempo ligado).
- **Metadados NTFS e conteúdo de arquivos** de um volume que você escolher verificar em busca
  de arquivos excluídos. Os volumes são abertos somente para leitura. Nomes, caminhos e
  pré-visualizações de arquivos aparecem apenas na janela do aplicativo.

Essas informações permanecem no seu computador e nunca são enviadas para lugar nenhum.

## Informações que o aplicativo armazena

O aplicativo grava um único arquivo local, o log de operações:

```
Log\operations.log   (ao lado do FreePartition.exe; %LocalAppData%\FreePartition se não for gravável)
```

Ele registra as ações colocadas na fila e aplicadas no aplicativo (por exemplo, uma formatação
ou um redimensionamento, com a letra da unidade, o tamanho e o horário) e os erros. Você pode
abri-lo ou excluí-lo a qualquer momento pelo menu Ferramentas ou pelo Explorador de Arquivos.
Ele nunca é enviado para lugar nenhum.

Os arquivos que você decidir recuperar são copiados somente para a pasta de destino que você
selecionar, em outra unidade.

## Acesso à rede

A única conexão de rede do FreePartition é a **verificação de atualização**. Ao iniciar (e quando
você escolhe **Ajuda > Check for updates**), o app consulta `api.github.com` para saber qual é a
versão mais recente do projeto. A requisição não leva dados pessoais, apenas o que qualquer
requisição web leva: seu endereço IP e o nome do app `FreePartition-Updater`. O GitHub pode
registrá-la conforme a sua
[declaração de privacidade](https://docs.github.com/pt/site-policy/privacy-policies/github-general-privacy-statement).

- Você pode desligar a verificação ao iniciar em **Help > Check for updates on startup**. A escolha
  fica salva em `settings.json`, ao lado do `FreePartition.exe` (ou em `%LocalAppData%\FreePartition`).
- Nada é baixado nem instalado até você clicar em **Update now**. O download vem das releases do
  projeto no GitHub e tem o SHA-256 conferido antes da instalação.
- A verificação não é feita no WinPE.

A outra ação externa é abrir um link ou uma pasta (como os links da janela Sobre ou o local do
log) com o programa padrão do Windows quando você clica. Esses sites são regidos por suas próprias
políticas de privacidade.

## Direitos de administrador

O aplicativo exige direitos de administrador (UAC) para acessar volumes brutos e executar
comandos de particionamento. Esses direitos são usados apenas nas operações de disco que você
solicitar.

## Privacidade de crianças

O aplicativo não coleta dados de ninguém, incluindo crianças.

## Alterações nesta política

Se o comportamento do aplicativo mudar de forma que afete a privacidade, este documento será
atualizado e a data acima será revisada. As versões anteriores estão disponíveis no histórico
do repositório.

## Contato

Dúvidas ou preocupações: abra uma issue em
[github.com/rfiori/FreePartition/issues](https://github.com/rfiori/FreePartition/issues).

## Isenção de responsabilidade

O FreePartition é fornecido sob a Licença MIT, sem garantia. Veja [LICENSE](../../LICENSE).
É um projeto independente e não é afiliado à EaseUS.
