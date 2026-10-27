using System;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// How a collection forgets the facts an upload sent, once the website confirms it stored them.
/// </summary>
/// <param name="ResponseKey">
/// The sync response key the server sends only after storing them (see
/// <see cref="CategoryInfo.StoredResponseKey"/>).
/// </param>
/// <param name="ForgetSent">Drops exactly the facts the upload sent. Framework thread only.</param>
public sealed record StoreConfirmation(string ResponseKey, Action ForgetSent);
