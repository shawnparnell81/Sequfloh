import { useState } from 'react';
import { Box, IconButton, Popover, Tooltip } from '@mui/material';
import SentimentSatisfiedOutlined from '@mui/icons-material/SentimentSatisfiedOutlined';

/** The same set the server accepts. Short codes expand to these before a message is sent. */
export const emojis = ['\uD83D\uDC4D', '\uD83D\uDC4E', '\uD83D\uDE00', '\uD83D\uDE02', '\uD83C\uDF89', '\u2705', '\uD83D\uDC40', '\uD83D\uDD25', '\uD83D\uDE4F', '\uD83D\uDCA1', '\u2764\uFE0F', '\uD83D\uDC4F', '\uD83D\uDE05', '\uD83E\uDD14', '\uD83D\uDE80'];

const codes: Record<string, string> = {
  thumbsup: emojis[0], '+1': emojis[0], thumbsdown: emojis[1], '-1': emojis[1], smile: emojis[2], grin: emojis[2],
  joy: emojis[3], laugh: emojis[3], tada: emojis[4], party: emojis[4], check: emojis[5], white_check_mark: emojis[5],
  eyes: emojis[6], fire: emojis[7], pray: emojis[8], bulb: emojis[9], idea: emojis[9], heart: emojis[10],
  clap: emojis[11], sweat_smile: emojis[12], thinking: emojis[13], rocket: emojis[14],
};

/** Turns :smile: into the emoji, including one typed at the end of the line. */
export function expandEmoji(text: string) {
  return text.replace(/:([a-z0-9_+-]+):/gi, (all, name: string) => codes[name.toLowerCase()] ?? all);
}

export function EmojiButton({ onPick, label = 'Add an emoji' }: { onPick: (emoji: string) => void; label?: string }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  return (
    <>
      <Tooltip title={label}>
        <IconButton size="small" aria-label={label} onClick={(e) => setAnchor(e.currentTarget)} data-testid="emoji-button"><SentimentSatisfiedOutlined fontSize="small" /></IconButton>
      </Tooltip>
      <Popover open={!!anchor} anchorEl={anchor} onClose={() => setAnchor(null)} anchorOrigin={{ vertical: 'top', horizontal: 'left' }} transformOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        slotProps={{ paper: { sx: { p: 1, width: 280, borderRadius: 3 } } }}>
        <Box data-testid="emoji-picker" sx={{ display: 'grid', gridTemplateColumns: 'repeat(5, 1fr)', gap: 0.5 }}>
          {emojis.map((e) => (
            <IconButton key={e} aria-label={e} onClick={() => { onPick(e); setAnchor(null); }} sx={{ fontSize: 22 }}>{e}</IconButton>
          ))}
        </Box>
      </Popover>
    </>
  );
}
