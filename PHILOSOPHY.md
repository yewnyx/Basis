# Philosophy of Basis

> [!CAUTION]
> This is document is under heavy discussion and iteration, and should not (yet) be taken as the official position of the project. It was merged before a full discussion had taken place because dooly likes shiny merge buttons and couldn't resist pressing it. Stay tuned for updates.

## What is Basis?

Basis is an open source MIT licensed framework for building immersive VR games.
It lowers the barrier of entry for game developers and is the *basis* upon which
those games can build.

To be specific, Basis is a **set of libraries** designed to help you bootstrap your VR
projects. Basis is also a **reference implementation** that demonstrates how
these components can work together.

It is built on top of Unity and C#, and the reference implementation in particular makes use of the Universal Rendering Pipeline (URP) for rendering, and targets IL2CPP.

## Who is this for?

This is for game developers and creators. Most but not all people interested in
basis fit into one of these categories:

- You want to build a singleplayer VR game with high quality full-body tracking
  and avatars
- You want to build a multiplayer social VR platform
- You want to host events in VR and bypass limitations of other platforms
- You want to contribute to FOSS (Free and Open Source Software)
- You want to tinker, experiment, and create

In short, Basis is whatever you want it to be, minus the hard foundational work
that limits your creativity or agency. Use basis as a starting point to build
what you want.

A rising tide lifts all ships. By providing a foundation to support VR
projects, creators can focus on their strengths while minimizing the impact of
their weaknesses.


## What are the project goals?

Basis seeks to build best-in-class implementations for the following features:

- **Presence**: The feeling, based on your passive senses, that you exist in a space.  
- **Spatialization**: The feeling that phenomena (especially your actions) exist in, and have an active effect on, objects in physical space.  
- **Embodiment**: The association between a representation of a body and the real body itself, created by continuous feedback between action and reaction within a physical body.  
- Social Features: *apis and frameworks* for multiplayer features, such as authentication to instances, instance moderation, asset retrieval, etc.

Basis aims to offer these building blocks to creators.

## Non-goals

There are some features which arguable are useful but which it is best to plan
to avoid including as a part of the core project. This is intentionally done to
limit project scope and avoid tying it to closely to any one approach or
platform.

In particular, the reference implementation does not plan to include the following:
* Content Delivery Networks - instead of us building backend services for a
  CDN, you are responsible for hosting your own content on your own CDN. We will
  provide batteries included integrations for popular CDNs, but building a CDN
  ourselves or moderating content on it is out of scope.
* Paid features - the reference implementation is not a profit vehicle and we
  have no plans to monetize it with paid features. All code in the reference
  implementation will be free and open source, and distributed for free. We *may*
  provide paid hosting services for non-technical users to spin up their own
  servers, but all of this will be FOSS MIT licensed software.
* Non-realtime social features - we do not plan to build any services other than what
  is strictly necessary for authentication, moderating an instance, and 

### Anti-plans

> [!IMPORTANT]
> To be **absolutely clear**: these are **anti-plans** and **design choices** for **the core project**. This sets expectations on **core project development**. Basis is anything you want it to be, as long as you take responsibility for your additions. There is no desire to intentionally obstruct third-party work.

- **No plans for In-App Purchases or freemium features**: Basis does not have plans to monetize the client. Engineering systems for in-app purchases, virtual economies, or paid content distribution is not a goal for the core project.
- **No plans for a centralized social layer**: There is no global network, matchmaking, or pre-built social system. Projects using Basis's reference implementation are entirely self-hosted.
- **No plans for a content delivery network (CDN)**: Basis does not include upload services for sharing avatars or worlds. These logistics are left to individual creators. Add-on services integrated by third parties are neither automatically included nor excluded.

In principle, building and maintaining centralized features requires centralized financial and technical support. If done poorly, these features would burden the project with maintenance issues. If done well, they would likely be closed off and monetized, defeating the purpose of Basis as an open toolset to empower creators.  

### Social features

Basis is not a platform, but for as long as Basis aims to provide functionality essential for VR experiences, particularly networked VR experiences, comparisons to social VR platforms are unavoidable.

We believe that on top of presence, spatialization, and embodiment, there is a monumental amount of work involved in producing a social VR platform. Those elements include a great deal of social infrastructure. This includes, but is not limited to:

- Moderation
- Safety and security measures 
- Community-building features 
- Active outreach to improve all of the above
- Marketing

These efforts extend far beyond the technical foundation of embodiment, presence, and spatialization. Basis is focused on that foundation. 

Building and maintaining a successful platform is an immense undertaking, requiring years of development, infrastructure, and user trust. The additional responsibilities of creating or maintaining a social network are far outside the scope of Basis, and far too much to take on. There is no current plan for the core Basis project to provide these systems.

> [!NOTE]
> Nothing stops others from doing so themselves, of course, but it is a far greater challenge than most expect.

tl;dr: Basis is not a shortcut to competing with established social platforms. Yes, in a "social VR dark age" (e.g., if major platforms disappeared), Basis could theoretically be used as a tool to restore the bare bones of social VR, but fundamentally, Basis is about flexibility and agency. It provides the tools and leaves the rest to you. You are free to take full control of your project - and with that freedom comes the responsibility to manage and maintain it.

### Social features, pt. 2

In addition to, presence, spatialization, and embodiment, there is a fourth foundational element to VR experiences, in particular when they are networked:

- **Identity**: The ability to maintain a consistent representation of yourself that can be shared with and perceived by others.  

A key component of existing in a VR space where you expect to interact with others (e.g., a four-player co-op game) is having a name and an avatar (or avatars) that you can carry with you across experiences.  

By far the easiest way to represent this is through a centralized infrastructure and API where users log in and upload an avatar. While pragmatic, this approach ties identity to the centralization of social infrastructure. Decentralized solutions - like those seen in BlueSky or Mastodon - aim to decouple identity from centralized login systems, and are worthy of research.  

This is an area of active investigation by a working group of people associated with the project, but does not have the intent to create a centralized social platform or infrastructure.

## I come from the future. You lied!

This is an open project. Sometimes, things change. The principles of the project may be reinterpreted, or shift according to contributions from motivated developers and artists. If you want to participate in the conversation, join our discord and get involved!
