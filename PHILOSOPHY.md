# Philosophy of Basis

> [!CAUTION]
> This is document is under heavy discussion and iteration, and should not (yet) be taken as the official position of the project, until this notice is removed

> [!NOTE]
> This is a living document, and subject to change. The principles the project follows may be reinterpreted, or shift according to contributions from motivated developers or artists. If you want to participate in the conversation, join our discord and get involved!

## What is Basis?

Basis is an open source MIT licensed framework for building immersive VR games. It lowers the barrier of entry for game developers and is the *basis* upon which those games can build.

Specifically, Basis is 1) a *set of libraries* designed to help you bootstrap your VR projects, and 2) a *reference implementation* that demonstrates how these components can work together.

It is built on top of Unity and C#, and the reference implementation in particular makes use of the Universal Rendering Pipeline (URP) for rendering, and targets IL2CPP.

## Who is this for?

* You want to create gameplay. You'll take your pick of technologies to use, but don't want to be your own engine programmer.
* You're a tech artist who wants to create and share something not limited by your current restrictions.
* You want to host an event in VR.
* All or none of the above: you want to tinker, experiment, and create with a toolset that gives you the shortcuts you need and saves you time.

## What are the project goals?

It may be helpful to break down what makes a good VR experience into some of its qualitative elements:

- **Presence**: The feeling, based on your passive senses, that you exist in a space.  
- **Spatialization**: The feeling that phenomena (especially your actions) exist in, and have an active effect on, objects in physical space.  
- **Embodiment**: The association between a representation of a body and the real body itself, created by continuous feedback between action and reaction within a physical body.  

To apply these ideas concretely, we can come up with features and areas of work that service them. For example, the Basis toolkit can help provide creators with:

- **Character Controllers**: The IK (Inverse Kinematics) - the code that drives avatars' skeleton and movement is very clearly a part of embodiment
- **Sound and VOIP**: Playing sounds in a way that is responsive to a dynamic environment is a part of immersion. Additionally, in-game communication is heavily influenced by gameplay direction: full audio spatialization, "radio" communications (and interference), or team channels.
- **Multiplayer**: Synchronizing physical or logical state between clients, local moderation actions such as server-kicks and bans.
- **Asset bundling**: Transmitting scenes or avatars can be challenging to do correctly, especially when it interacts with the character controllers and multiplayer functions.

Basis's number-one goal is to build tools to empower creators with these principles in mind.

## What are some **non-goals** for Basis?

Features which incentivize centralization or large-network-scale social infrastructure are considered out of scope of the framework and reference implementation. This may include:

### Centralized services

Basis prioritizes standalone or self-hostable experiences. Building and maintaining centralized services requires centralized financial and technical support. If done poorly, these features would burden the project with maintenance issues. If done well, they would likely be closed off and monetized, defeating the purpose of Basis as an open toolset. Basis seeks to have minimal, if any, dependencies on external servers. Solutions for problems that extend beyond the client-serveer relationship will first consider federated, P2P, or self-hostable mechanisms.

### CDNs (Content Delivery Networks)

Creating a streamlined experience for uploading content and providing access to it is very convenient, but creates a big single point of failure: who pays for it? Many questions follow: Who takes responsibility for the content? How long does it stay up for? Who has the right to delete it?

The answers to these questions are considered out of scope; the allocation of responsibility for content hosting is left to creators.

### Paid features

The reference implementation should remain free and open-source, and not offer a freemium model that restricts functionality. 

> [!NOTE]
> This is not an objection to paid services such as dedicated server hosts, or revenue generation in general.

## Non-realtime social features

Services other than what is strictly necessary for a good multiplayer experience in a realtime instance are out of scope. Identity and local moderation are in-scope, because these are very useful tools to facilitate a healthy server environment. 

- "Identity" means the ability to maintain a consistent representation of yourself that can be shared with and perceived by others. For example, a gamertag, or at a lower level, a decentralized identifier (think "SteamIDs, but distributed" - or bluesky if you're familiar with their protocols)
- "Local moderation" means server kicks and bans, or other similar actions that have a clear manifestation to players at the level of a single server.