export const en = {
  tabs: { today: 'Today', progress: 'Progress', coach: 'Coach' },
  settings: {
    title: 'Settings',
    open: 'Settings',
    account: 'Account',
    signOut: 'Sign out',
  },
  today: {
    emptyTitle: 'Nothing planned yet',
    emptyBody: 'Your Actions for the day will show up here once you have a Plan.',
  },
  progress: {
    emptyTitle: 'No Aspirations yet',
    emptyBody: 'Each Aspiration you add gets its own card here, with your Pace and Projection.',
  },
  coach: {
    emptyTitle: 'Your Coach is here when you are ready',
    emptyBody: 'Say hello below. Conversations with your Coach will appear here.',
    inputPlaceholder: 'Message your Coach',
    send: 'Send',
    typing: 'Your Coach is typing',
    saved: 'Saved · your Coach will reply when it can',
    lateStrip: "Your Coach can't reply right now. Your messages are saved.",
    today: 'Today',
    yesterday: 'Yesterday',
  },
  signIn: {
    title: 'Waypoint',
    tagline: 'Name what you want. Your Coach charts the route and walks it with you.',
    continueWithGoogle: 'Continue with Google',
    failed: 'We could not sign you in. Please try again.',
    offline: 'Waypoint could not reach the server. Check your connection and try again.',
  },
  common: { loading: 'Loading…' },
} as const;
