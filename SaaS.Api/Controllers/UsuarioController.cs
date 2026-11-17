using SaaS.Domain.Models;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Application.Exceptions;
using SaaS.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using SaaS.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using SaaS.Api.Security;
using SaaS.Api.Services;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsuarioController : ControllerBase
    {
        private readonly IUsuarioRepository _usuarioRepository;
        private readonly PlanoAcessoService? _planos;
        private readonly ILocalizacaoCadastroService _localizacaoCadastroService;
        private readonly IEmailService _emailService;
        private readonly ILogger<UsuarioController> _logger;
        private readonly IArquivoUploadService? _arquivoUploadService;

        public UsuarioController(
            IUsuarioRepository usuarioRepository,
            ILocalizacaoCadastroService localizacaoCadastroService,
            IEmailService emailService,
            ILogger<UsuarioController>? logger = null,
            IArquivoUploadService? arquivoUploadService = null,
            PlanoAcessoService? planos = null)
        {
            _usuarioRepository = usuarioRepository;
            _planos = planos;
            _localizacaoCadastroService = localizacaoCadastroService;
            _emailService = emailService;
            _logger = logger ?? NullLogger<UsuarioController>.Instance;
            _arquivoUploadService = arquivoUploadService;
        }

        [HttpPost("InserirUsuario")]
        [HttpPost("InserirProfissionalEmpresa")]
        [AllowAnonymous]
        [EnableRateLimiting("registration")]
        [RequestSizeLimit(26 * 1024 * 1024)]
        public async Task<IActionResult> InserirUsuario([FromForm] CreateUsuarioModel createUsuario)
        {
            if (createUsuario == null)
                return BadRequest("Dados do usuário inválidos.");

            string? caminhoFoto = null;
            var uploadVinculadoAoUsuario = false;

            try
            {
                if (!createUsuario.Latitude.HasValue || !createUsuario.Longitude.HasValue)
                    return BadRequest("Latitude e longitude sao obrigatorias para concluir o cadastro.");

                var coordenadas = new CoordenadasModel
                {
                    Latitude = createUsuario.Latitude.Value,
                    Longitude = createUsuario.Longitude.Value
                };
                var localizacao = await _localizacaoCadastroService.ResolverAutomaticamenteAsync(
                    coordenadas,
                    HttpContext.RequestAborted);

                createUsuario.CidadeFk = localizacao.CidadeId;

                if (Request.HasFormContentType &&
                    Request.Form.TryGetValue(nameof(CreateUsuarioModel.TipoUsuario), out var tipoUsuarioForm) &&
                    int.TryParse(tipoUsuarioForm.FirstOrDefault()?.Trim(), out _))
                {
                    return BadRequest("Tipo de usuário deve ser enviado como texto: CLIENTE, PROFISSIONAL, AUTONOMO ou EMPRESA.");
                }

                if (createUsuario.TipoUsuario == null)
                    return BadRequest("Selecione um tipo de usuário válido.");

                var tipoUsuario = createUsuario.TipoUsuario.Value;
                var cadastroPelaEmpresa = HttpContext.Request.Path.Value?.EndsWith(
                    "/InserirProfissionalEmpresa", StringComparison.OrdinalIgnoreCase) == true;
                if (cadastroPelaEmpresa && tipoUsuario != TipoUsuario.PROFISSIONAL)
                    return BadRequest("Esta rota e exclusiva para o cadastro de profissionais pela empresa.");

                // Cadastro publico nunca aprova o proprio vinculo, mesmo com cookie de login.
                var statusVinculoProfissional = tipoUsuario == TipoUsuario.PROFISSIONAL && !cadastroPelaEmpresa
                    ? "PENDENTE"
                    : "APROVADO";
                // var empresaCriandoProfissional = false;
                string? senhaProfissionalGerada = null;
                if (tipoUsuario == TipoUsuario.PROFISSIONAL)
                {
                    if (!createUsuario.EmpresaId.HasValue || createUsuario.EmpresaId.Value <= 0)
                        return BadRequest("Selecione a empresa para solicitar o vinculo.");

                    if (cadastroPelaEmpresa)
                    {
                        if (User.Identity?.IsAuthenticated != true)
                            return Unauthorized(new { message = "A empresa deve estar autenticada." });
                        if (!User.IsInRole(nameof(TipoUsuario.EMPRESA)) ||
                            !User.EhProprioUsuario(createUsuario.EmpresaId.Value))
                        {
                            return Forbid();
                        }

                        if (_planos != null)
                        {
                            var acessoEmpresa = await _planos.ConsultarAsync(createUsuario.EmpresaId.Value);
                            if (!acessoEmpresa.AcessoAtivo ||
                                await _planos.ContarProfissionaisAsync(createUsuario.EmpresaId.Value) >= acessoEmpresa.LimiteProfissionais)
                                return StatusCode(403, new { message = "Assinatura inativa ou limite de profissionais atingido." });
                        }

                        // empresaCriandoProfissional = true;
                        statusVinculoProfissional = "APROVADO";
                        senhaProfissionalGerada = GerarSenhaTemporaria();
                        createUsuario.Senha = senhaProfissionalGerada;

                        var profissionalExistente = string.IsNullOrWhiteSpace(createUsuario.Email)
                            ? null
                            : await _usuarioRepository.ListarUsuarioPorEmail(createUsuario.Email);

                        if (profissionalExistente != null)
                        {
                            var statusAnterior = profissionalExistente.TipoUsuario == TipoUsuario.PROFISSIONAL
                                ? await _usuarioRepository.ObterStatusVinculoProfissionalEmpresa(
                                    profissionalExistente.Id,
                                    createUsuario.EmpresaId.Value)
                                : null;

                            if (statusAnterior == null)
                            {
                                return Conflict(new
                                {
                                    code = "EMAIL_EM_USO",
                                    message = "O email informado ja pertence a outro usuario ou a um profissional sem vinculo com esta empresa."
                                });
                            }

                            var acessoAtualizado = await _usuarioRepository.AtualizarAcessoProfissionalEmpresa(
                                profissionalExistente.Id,
                                createUsuario.EmpresaId.Value,
                                senhaProfissionalGerada);

                            if (!acessoAtualizado)
                            {
                                return Conflict(new
                                {
                                    code = "VINCULO_NAO_ENCONTRADO",
                                    message = "O profissional existe, mas nao possui vinculo com esta empresa."
                                });
                            }

                            // Envio de credenciais por e-mail desativado enquanto o SMTP
                            // nao estiver disponivel na hospedagem.
                            // try
                            // {
                            //     await _emailService.EnviarCredenciaisProfissionalAsync(
                            //         profissionalExistente.Email!,
                            //         profissionalExistente.Email!,
                            //         senhaProfissionalGerada,
                            //         profissionalExistente.Nome);
                            // }
                            // catch (EmailEnvioException)
                            // {
                            //     await _usuarioRepository.RestaurarAcessoProfissionalEmpresa(
                            //         profissionalExistente.Id,
                            //         createUsuario.EmpresaId.Value,
                            //         profissionalExistente.Senha,
                            //         statusAnterior);
                            //     throw;
                            // }

                            profissionalExistente.VinculoStatus = "APROVADO";
                            Response.Headers.CacheControl = "no-store";
                            return Ok(new
                            {
                                profissional = CriarRespostaUsuario(profissionalExistente),
                                emailEnviado = false,
                                acessoReenviado = true,
                                senhaTemporaria = senhaProfissionalGerada
                            });
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(createUsuario.Senha) || createUsuario.Senha.Length < 8)
                    return BadRequest("A senha deve ter pelo menos 8 caracteres.");

                if (tipoUsuario == TipoUsuario.CLIENTE)
                {
                    if (string.IsNullOrWhiteSpace(createUsuario.Email) &&
                        string.IsNullOrWhiteSpace(createUsuario.Telefone))
                    {
                        return BadRequest("Informe email ou telefone para cadastrar o cliente.");
                    }
                }
                else if (string.IsNullOrWhiteSpace(createUsuario.Email))
                {
                    return BadRequest("Email é obrigatório para esse tipo de usuário.");
                }

                if (createUsuario.FotoPerfil != null && createUsuario.FotoPerfil.Length > 0)
                {
                    caminhoFoto = await SalvarFotoComOriginalAsync(createUsuario.FotoPerfil, createUsuario.FotoOriginal);
                }

                var novoUsuario = tipoUsuario == TipoUsuario.EMPRESA
                ? new EmpresaModel
                {
                    Nome = createUsuario.Nome,
                    Email = createUsuario.Email,
                    Telefone = createUsuario.Telefone,
                    Slug = createUsuario.Slug,
                    Senha = createUsuario.Senha!,
                    FotoPerfil = caminhoFoto,
                    TipoUsuario = tipoUsuario,
                    AssinaturaAtiva = StatusAssinatura.NAO_ATIVA,
                    DataFimAssinatura = null,
                    DataCriacao = createUsuario.DataCriacao,
                    DataAlteracao = createUsuario.DataAlteracao,
                    CidadeFk = createUsuario.CidadeFk,
                    Cnpj = createUsuario.Cnpj,
                    NomeFantasia = createUsuario.NomeFantasia
                }
                : new UsuarioModel
                {
                    Nome = createUsuario.Nome,
                    Email = createUsuario.Email,
                    Telefone = createUsuario.Telefone,
                    Slug = createUsuario.Slug,
                    Senha = createUsuario.Senha!,
                    FotoPerfil = caminhoFoto,
                    TipoUsuario = tipoUsuario,
                    AssinaturaAtiva = StatusAssinatura.NAO_ATIVA,
                    DataFimAssinatura = null,
                    DataCriacao = createUsuario.DataCriacao,
                    DataAlteracao = createUsuario.DataAlteracao,
                    CidadeFk = createUsuario.CidadeFk,
                };

                var usuarioCriado = await _usuarioRepository.InserirUsuario(
                    novoUsuario,
                    createUsuario.EmpresaId,
                    statusVinculoProfissional,
                    coordenadas);
                uploadVinculadoAoUsuario = true;

                if (tipoUsuario == TipoUsuario.PROFISSIONAL)
                    usuarioCriado.VinculoStatus = statusVinculoProfissional;

                // Envio de credenciais por e-mail desativado enquanto o SMTP
                // nao estiver disponivel na hospedagem.
                // if (empresaCriandoProfissional && senhaProfissionalGerada != null)
                // {
                //     await _emailService.EnviarCredenciaisProfissionalAsync(
                //         usuarioCriado.Email!,
                //         usuarioCriado.Email!,
                //         senhaProfissionalGerada,
                //         usuarioCriado.Nome);
                // }

                var respostaUsuario = CriarRespostaUsuario(usuarioCriado);
                if (tipoUsuario == TipoUsuario.PROFISSIONAL && senhaProfissionalGerada != null)
                {
                    Response.Headers.CacheControl = "no-store";
                    return CreatedAtAction(
                        nameof(ListarUsuarioPorId),
                        new { id = usuarioCriado.Id },
                        new
                        {
                            profissional = respostaUsuario,
                            senhaTemporaria = senhaProfissionalGerada
                        });
                }

                return CreatedAtAction(
                    nameof(ListarUsuarioPorId),
                    new { id = usuarioCriado.Id },
                    respostaUsuario);
            }
            catch (ArgumentException ex)
            {
                if (!uploadVinculadoAoUsuario)
                    await RemoverFoto(caminhoFoto);
                _logger.LogWarning(ex, "Cadastro de usuario recusado por dados invalidos.");
                return BadRequest(new
                {
                    code = "DADOS_INVALIDOS",
                    message = $"Erro ao criar o usuario: {ex.Message}"
                });
            }
            catch (GeocodingIndisponivelException)
            {
                if (!uploadVinculadoAoUsuario)
                    await RemoverFoto(caminhoFoto);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    "Nao foi possivel identificar sua localizacao agora. Tente novamente.");
            }
            catch (EmailEnvioException ex)
            {
                if (!uploadVinculadoAoUsuario)
                    await RemoverFoto(caminhoFoto);
                _logger.LogError(ex, "Falha ao enviar as credenciais do profissional por SMTP.");
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        code = "EMAIL_NAO_ENVIADO",
                        message = "O cadastro foi processado, mas o email de acesso nao foi enviado. Verifique as configuracoes SMTP e tente novamente com o mesmo email."
                    });
            }
            catch (Exception ex)
            {
                if (!uploadVinculadoAoUsuario)
                    await RemoverFoto(caminhoFoto);
                _logger.LogError(ex, "Falha inesperada ao criar usuario.");
                return StatusCode(500, new
                {
                    code = "ERRO_INTERNO",
                    message = "Nao foi possivel criar o usuario."
                });
            }
        }

        [HttpGet("ListarUsuarios")]
        [Authorize]
        public async Task<IActionResult> ListarUsuarios()
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Forbid();

            try
            {
                var usuario = await _usuarioRepository.ListarUsuarioPorId(usuarioId);
                return usuario == null
                    ? NotFound("Usuário não encontrado.")
                    : Ok(new[] { CriarRespostaUsuario(usuario) });
            }
            catch (Exception)
            {
                return StatusCode(500, "Nao foi possivel obter o usuario.");
            }
        }

        [HttpGet("{id:int}")]
        [Authorize]
        public async Task<IActionResult> ListarUsuarioPorId(int id)
        {
            if (!User.EhProprioUsuario(id))
                return Forbid();

            try
            {
                var usuario = await _usuarioRepository.ListarUsuarioPorId(id);
                if (usuario == null)
                    return NotFound("Usuário não encontrado.");

                return Ok(CriarRespostaUsuario(usuario));
            }
            catch (Exception)
            {
                return StatusCode(500, "Nao foi possivel obter o usuario.");
            }
        }

        [HttpPut("{id}/dados-basicos")]
        [HttpPatch("{id}/dados-basicos")]
        [Authorize]
        public async Task<IActionResult> AtualizarDadosBasicos(int id, [FromBody] UpdateUsuarioModel request)
        {
            if (!User.EhProprioUsuario(id))
                return Forbid();

            if (request == null)
                return BadRequest("Dados do usuário inválidos.");

            if (string.IsNullOrWhiteSpace(request.Nome) &&
                string.IsNullOrWhiteSpace(request.Email) &&
                string.IsNullOrWhiteSpace(request.Senha))
            {
                return BadRequest("Informe ao menos um campo para atualizar: nome, email ou senha.");
            }

            if (!string.IsNullOrWhiteSpace(request.Senha) && request.Senha.Length < 8)
                return BadRequest("A senha deve ter pelo menos 8 caracteres.");

            try
            {
                var usuarioAtualizado = await _usuarioRepository.AtualizarDadosUsuario(id, request.Nome, request.Email, request.Senha);
                if (!usuarioAtualizado)
                    return NotFound("Usuário não encontrado.");

                return Ok(usuarioAtualizado);
            }
            catch (Exception)
            {
                return StatusCode(500, "Nao foi possivel atualizar os dados.");
            }
        }

        /// <summary>
        /// Troca a senha do cliente autenticado somente depois de validar a senha atual.
        /// O ID e obtido dos claims da sessao, nunca do payload.
        /// </summary>
        [HttpPost("cliente/alterar-senha")]
        [Authorize(Roles = "CLIENTE")]
        [EnableRateLimiting("password-recovery")]
        public async Task<IActionResult> AlterarSenhaCliente(
            [FromBody] AlterarSenhaClienteModel? request,
            [FromServices] IPasswordHasher<UsuarioModel> passwordHasher)
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Unauthorized(new { message = "Sessao invalida." });

            if (request == null ||
                string.IsNullOrEmpty(request.SenhaAtual) ||
                string.IsNullOrEmpty(request.ConfirmarSenhaAtual) ||
                string.IsNullOrEmpty(request.NovaSenha))
                return BadRequest(new { message = "Informe a senha atual, sua confirmacao e a nova senha." });

            if (!string.Equals(request.SenhaAtual, request.ConfirmarSenhaAtual, StringComparison.Ordinal))
                return BadRequest(new { message = "A confirmacao da senha atual nao confere." });

            if (request.NovaSenha.Length < 8)
                return BadRequest(new { message = "A nova senha deve ter pelo menos 8 caracteres." });

            if (string.Equals(request.SenhaAtual, request.NovaSenha, StringComparison.Ordinal))
                return BadRequest(new { message = "A nova senha deve ser diferente da senha atual." });

            var usuario = await _usuarioRepository.ListarUsuarioPorId(usuarioId);
            if (usuario == null || usuario.TipoUsuario != TipoUsuario.CLIENTE)
                return NotFound(new { message = "Conta nao encontrada." });

            var verificacao = passwordHasher.VerifyHashedPassword(usuario, usuario.Senha, request.SenhaAtual);
            var senhaLegadaCorreta = verificacao == PasswordVerificationResult.Failed &&
                LegacyPasswordHasher.VerificarSenha(request.SenhaAtual, usuario.Senha);

            if (verificacao == PasswordVerificationResult.Failed && !senhaLegadaCorreta)
                return BadRequest(new { message = "Senha atual incorreta." });

            var novoHash = passwordHasher.HashPassword(usuario, request.NovaSenha);
            await _usuarioRepository.AtualizarSenhaHash(usuarioId, novoHash);
            return Ok(new { message = "Senha alterada com sucesso." });
        }

        // Empresa pode editar apenas os profissionais vinculados e aprovados.
        [HttpPut("empresa/profissionais/{id:int}")]
        [Authorize(Roles = "EMPRESA")]
        public async Task<IActionResult> EditarProfissionalEmpresa(int id, [FromBody] UpdatePerfilModel request)
        {
            if (!User.TryGetUsuarioId(out var empresaId))
                return Forbid();
            if (request == null || string.IsNullOrWhiteSpace(request.Nome) ||
                string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email.Trim(), out _))
                return BadRequest(new { erro = "Informe nome e email validos." });
            if (!string.IsNullOrWhiteSpace(request.Telefone))
            {
                var digits = new string(request.Telefone.Where(char.IsDigit).ToArray());
                if (digits.Length is < 10 or > 11)
                    return BadRequest(new { erro = "Informe um telefone valido com DDD." });
            }
            var vinculo = await _usuarioRepository.ObterStatusVinculoProfissionalEmpresa(id, empresaId);
            if (!string.Equals(vinculo, "APROVADO", StringComparison.OrdinalIgnoreCase))
                return Forbid();
            try
            {
                var atualizado = await _usuarioRepository.AtualizarPerfilUsuario(
                    id, request.Nome.Trim(), request.Email.Trim(), request.Telefone, null, false);
                return atualizado == null ? NotFound() : Ok(CriarRespostaUsuario(atualizado));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
        }

        // Edicao pela empresa com foto opcional: preserva o endpoint JSON ja utilizado.
        // A imagem integral e privada: somente a empresa com vinculo aprovado pode acessa-la.
        [HttpGet("empresa/profissionais/{id:int}/foto-original")]
        [Authorize(Roles = "EMPRESA")]
        public async Task<IActionResult> ObterFotoOriginalProfissional(int id)
        {
            if (!User.TryGetUsuarioId(out var empresaId))
                return Forbid();

            var vinculo = await _usuarioRepository.ObterStatusVinculoProfissionalEmpresa(id, empresaId);
            if (!string.Equals(vinculo, "APROVADO", StringComparison.OrdinalIgnoreCase))
                return Forbid();

            var profissional = await _usuarioRepository.ListarUsuarioPorId(id);
            if (profissional == null || string.IsNullOrWhiteSpace(profissional.FotoPerfil)
                || _arquivoUploadService == null)
                return NotFound();

            var original = await _arquivoUploadService.ObterOriginalAsync(
                profissional.FotoPerfil, HttpContext.RequestAborted);
            if (original == null)
                return NotFound();

            Response.Headers.CacheControl = "no-store";
            return File(original.Conteudo, original.TipoConteudo);
        }

        [HttpPut("empresa/profissionais/{id:int}/com-foto")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "EMPRESA")]
        [RequestSizeLimit(26 * 1024 * 1024)]
        public async Task<IActionResult> EditarProfissionalEmpresaComFoto(int id, [FromForm] UpdatePerfilModel request)
        {
            if (!User.TryGetUsuarioId(out var empresaId))
                return Forbid();

            if (request == null || string.IsNullOrWhiteSpace(request.Nome) ||
                string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email.Trim(), out _))
                return BadRequest(new { erro = "Informe nome e email validos." });

            if (!string.IsNullOrWhiteSpace(request.Telefone))
            {
                var digits = new string(request.Telefone.Where(char.IsDigit).ToArray());
                if (digits.Length is < 10 or > 11)
                    return BadRequest(new { erro = "Informe um telefone valido com DDD." });
            }

            var vinculo = await _usuarioRepository.ObterStatusVinculoProfissionalEmpresa(id, empresaId);
            if (!string.Equals(vinculo, "APROVADO", StringComparison.OrdinalIgnoreCase))
                return Forbid();

            string? caminhoFoto = null;
            try
            {
                var usuarioAtual = await _usuarioRepository.ListarUsuarioPorId(id);
                if (usuarioAtual == null)
                    return NotFound("Profissional nao encontrado.");

                caminhoFoto = await SalvarFotoComOriginalAsync(request.FotoPerfil, request.FotoOriginal);
                var atualizarFoto = caminhoFoto != null;
                var atualizado = await _usuarioRepository.AtualizarPerfilUsuario(
                    id,
                    request.Nome.Trim(),
                    request.Email.Trim(),
                    request.Telefone,
                    caminhoFoto,
                    atualizarFoto);

                if (atualizado == null)
                {
                    await RemoverFoto(caminhoFoto);
                    return NotFound("Profissional nao encontrado.");
                }

                if (atualizarFoto)
                    await RemoverFoto(usuarioAtual.FotoPerfil);

                return Ok(CriarRespostaUsuario(atualizado));
            }
            catch (ArgumentException ex)
            {
                await RemoverFoto(caminhoFoto);
                return BadRequest(new { erro = ex.Message });
            }
            catch
            {
                await RemoverFoto(caminhoFoto);
                throw;
            }
        }

        // Imagem de origem reservada ao titular do perfil, independente do papel.
        [HttpGet("{id:int}/foto-original")]
        [Authorize(Roles = "EMPRESA,AUTONOMO,PROFISSIONAL")]
        public async Task<IActionResult> ObterFotoOriginalPropria(int id)
        {
            if (!User.EhProprioUsuario(id))
                return Forbid();

            var usuario = await _usuarioRepository.ListarUsuarioPorId(id);
            if (usuario == null || string.IsNullOrWhiteSpace(usuario.FotoPerfil)
                || _arquivoUploadService == null)
                return NotFound();

            var original = await _arquivoUploadService.ObterOriginalAsync(
                usuario.FotoPerfil, HttpContext.RequestAborted);
            if (original == null)
                return NotFound();

            Response.Headers.CacheControl = "no-store";
            return File(original.Conteudo, original.TipoConteudo);
        }

        [HttpPut("{id:int}/perfil")]
        [HttpPatch("{id:int}/perfil")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "EMPRESA,AUTONOMO,PROFISSIONAL,CLIENTE")]
        [RequestSizeLimit(26 * 1024 * 1024)]
        public async Task<IActionResult> AtualizarPerfil(int id, [FromForm] UpdatePerfilModel request)
        {
            if (!User.EhProprioUsuario(id))
                return Forbid();

            if (request == null || string.IsNullOrWhiteSpace(request.Nome))
                return BadRequest("Informe o nome.");

            var cliente = User.IsInRole(nameof(TipoUsuario.CLIENTE));
            var possuiEmail = !string.IsNullOrWhiteSpace(request.Email);
            var possuiTelefone = !string.IsNullOrWhiteSpace(request.Telefone);
            if ((!cliente && !possuiEmail) ||
                (possuiEmail && !MailAddress.TryCreate(request.Email.Trim(), out _)))
                return BadRequest("Informe um email valido.");

            // Clientes podem se cadastrar usando apenas o telefone.
            if (cliente && !possuiEmail && !possuiTelefone)
                return BadRequest("Informe um email ou telefone.");

            if (!string.IsNullOrWhiteSpace(request.Telefone))
            {
                var telefone = new string(request.Telefone.Where(char.IsDigit).ToArray());
                if (telefone.Length is < 10 or > 11)
                    return BadRequest("Informe um telefone valido com DDD.");
            }

            string? caminhoFoto = null;

            try
            {
                var usuarioAtual = await _usuarioRepository.ListarUsuarioPorId(id);
                if (usuarioAtual == null)
                    return NotFound("Usuario nao encontrado.");

                if (request.Cnpj is not null)
                {
                    if (usuarioAtual.TipoUsuario != TipoUsuario.EMPRESA)
                        return BadRequest("Somente contas de empresa podem alterar o CNPJ.");

                    if (!CnpjValido(request.Cnpj))
                        return BadRequest("Informe um CNPJ valido, numerico ou alfanumerico.");
                }

                caminhoFoto = await SalvarFotoComOriginalAsync(request.FotoPerfil, request.FotoOriginal);
                var atualizarFoto = caminhoFoto != null;
                var atualizado = request.Cnpj is null
                    ? await _usuarioRepository.AtualizarPerfilUsuario(
                        id, request.Nome, request.Email, request.Telefone, caminhoFoto, atualizarFoto)
                    : await _usuarioRepository.AtualizarPerfilUsuario(
                        id, request.Nome, request.Email, request.Telefone, caminhoFoto, atualizarFoto, request.Cnpj);

                if (atualizado == null)
                {
                    await RemoverFoto(caminhoFoto);
                    return NotFound("Usuario nao encontrado.");
                }

                if (atualizarFoto)
                    await RemoverFoto(usuarioAtual.FotoPerfil);

                return Ok(CriarRespostaUsuario(atualizado));
            }
            catch (ArgumentException ex)
            {
                await RemoverFoto(caminhoFoto);
                return BadRequest(ex.Message);
            }
            catch
            {
                await RemoverFoto(caminhoFoto);
                throw;
            }
        }

        // Modelo numerico e novo formato alfanumerico: ASCII - 48 e modulo 11.
        private static bool CnpjValido(string valor)
        {
            var texto = valor.Trim().ToUpperInvariant();
            if (texto.Any(c => !(c >= '0' && c <= '9') &&
                               !(c >= 'A' && c <= 'Z') &&
                               c != '.' && c != '/' && c != '-' && c != ' '))
                return false;

            var cnpj = new string(texto.Where(c => c != '.' && c != '/' && c != '-' && c != ' ').ToArray());
            if (cnpj.Length != 14 ||
                cnpj.Take(12).Any(c => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z'))) ||
                cnpj.Skip(12).Any(c => c < '0' || c > '9') ||
                cnpj.Take(12).All(c => c == cnpj[0]))
                return false;

            static int Digito(string caracteres, int[] pesos)
            {
                var soma = pesos.Select((peso, indice) =>
                    (caracteres[indice] - 48) * peso).Sum();
                var resto = soma % 11;
                return resto < 2 ? 0 : 11 - resto;
            }

            var pesosPrimeiro = new[] { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
            var pesosSegundo = new[] { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };

            return cnpj[12] - '0' == Digito(cnpj, pesosPrimeiro) &&
                   cnpj[13] - '0' == Digito(cnpj, pesosSegundo);
        }

        private static object CriarRespostaUsuario(UsuarioModel usuario)
        {
            return new
            {
                usuario.Id,
                usuario.Nome,
                usuario.Email,
                usuario.Telefone,
                usuario.Slug,
                usuario.FotoPerfil,
                usuario.TipoUsuario,
                NomeFantasia = (usuario as EmpresaModel)?.NomeFantasia,
                Cnpj = (usuario as EmpresaModel)?.Cnpj,
                usuario.AssinaturaAtiva,
                usuario.DataFimAssinatura,
                usuario.CidadeFk,
                usuario.VinculoStatus
            };
        }

        private static async Task<bool> ArquivoImagemValido(IFormFile arquivo, string extensao)
        {
            var cabecalho = new byte[12];
            await using var stream = arquivo.OpenReadStream();
            var bytesLidos = await stream.ReadAsync(cabecalho);

            return extensao switch
            {
                ".jpg" or ".jpeg" =>
                    bytesLidos >= 3 && cabecalho[0] == 0xFF && cabecalho[1] == 0xD8 && cabecalho[2] == 0xFF,
                ".png" =>
                    bytesLidos >= 8 && cabecalho[..8].SequenceEqual(
                        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                ".webp" =>
                    bytesLidos >= 12 &&
                    Encoding.ASCII.GetString(cabecalho, 0, 4) == "RIFF" &&
                    Encoding.ASCII.GetString(cabecalho, 8, 4) == "WEBP",
                _ => false
            };
        }

        private static string GerarSenhaTemporaria()
        {
            const string minusculas = "abcdefghijkmnopqrstuvwxyz";
            const string maiusculas = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string numeros = "23456789";
            const string especiais = "!@$%*-_?";
            const string todos = minusculas + maiusculas + numeros + especiais;
            var senha = new char[16];

            senha[0] = minusculas[RandomNumberGenerator.GetInt32(minusculas.Length)];
            senha[1] = maiusculas[RandomNumberGenerator.GetInt32(maiusculas.Length)];
            senha[2] = numeros[RandomNumberGenerator.GetInt32(numeros.Length)];
            senha[3] = especiais[RandomNumberGenerator.GetInt32(especiais.Length)];

            for (var indice = 4; indice < senha.Length; indice++)
                senha[indice] = todos[RandomNumberGenerator.GetInt32(todos.Length)];

            for (var indice = senha.Length - 1; indice > 0; indice--)
            {
                var destino = RandomNumberGenerator.GetInt32(indice + 1);
                (senha[indice], senha[destino]) = (senha[destino], senha[indice]);
            }

            return new string(senha);
        }

        private async Task<string?> SalvarFotoComOriginalAsync(IFormFile? foto, IFormFile? original)
        {
            string? imagemEditada = null;
            string? imagemOriginal = null;
            try
            {
                imagemEditada = await SalvarFotoPerfil(foto);
                if (imagemEditada != null && original is { Length: > 0 })
                {
                    imagemOriginal = await SalvarFotoPerfil(original, 20 * 1024 * 1024);
                    if (imagemOriginal != null && _arquivoUploadService != null)
                        await _arquivoUploadService.VincularOriginalAsync(
                            imagemEditada, imagemOriginal, HttpContext.RequestAborted);
                }
                return imagemEditada;
            }
            catch
            {
                await RemoverFoto(imagemEditada);
                await RemoverFoto(imagemOriginal);
                throw;
            }
        }

        private async Task<string?> SalvarFotoPerfil(IFormFile? fotoPerfil, long tamanhoMaximo = 5 * 1024 * 1024)
        {
            if (fotoPerfil is not { Length: > 0 })
                return null;

            var extensao = Path.GetExtension(fotoPerfil.FileName).ToLowerInvariant();
            var extensoesPermitidas = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp" };
            var tiposPermitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg", "image/png", "image/webp"
            };

            if (fotoPerfil.Length > tamanhoMaximo ||
                !extensoesPermitidas.Contains(extensao) ||
                !tiposPermitidos.Contains(fotoPerfil.ContentType) ||
                !await ArquivoImagemValido(fotoPerfil, extensao))
            {
                throw new ArgumentException(
                    $"A foto deve ser JPG, PNG ou WEBP e ter no maximo {tamanhoMaximo / (1024 * 1024)} MB.");
            }

            if (_arquivoUploadService == null)
                throw new InvalidOperationException("O armazenamento de imagens nao foi configurado.");

            await using var stream = fotoPerfil.OpenReadStream();
            using var memoria = new MemoryStream();
            await stream.CopyToAsync(memoria, HttpContext.RequestAborted);

            return await _arquivoUploadService.SalvarAsync(
                memoria.ToArray(),
                extensao,
                fotoPerfil.ContentType,
                HttpContext.RequestAborted);
        }

        private async Task RemoverFoto(string? caminhoFoto)
        {
            if (string.IsNullOrWhiteSpace(caminhoFoto) || caminhoFoto == "SEM_FOTO")
                return;

            if (_arquivoUploadService != null)
                await _arquivoUploadService.RemoverAsync(caminhoFoto);

            var caminhoCompleto = Path.Combine(
                Directory.GetCurrentDirectory(),
                "wwwroot",
                caminhoFoto.Replace('/', Path.DirectorySeparatorChar));

            if (System.IO.File.Exists(caminhoCompleto))
                System.IO.File.Delete(caminhoCompleto);
        }
    }
}
